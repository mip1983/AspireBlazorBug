using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

return await ReproduceAsync();

static async Task<int> ReproduceAsync()
{
    var timeout = TimeSpan.FromSeconds(2);
    Console.WriteLine("Aspire Blazor origin-resolution repro");
    Console.WriteLine("No host is started and no network requests are made.");
    Console.WriteLine();

    var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
    {
        Args = ["--publisher", "manifest"],
        ProjectDirectory = AppContext.BaseDirectory
    });
    var api = builder.AddResource(new ProjectResource("api"))
        .WithHttpEndpoint(targetPort: 8080);
    var web = builder.AddResource(new ProjectResource("web"))
        .WithHttpsEndpoint(targetPort: 8080)
        .ProxyBlazorService(api);

    var endpoint = web.Resource.Annotations.OfType<EndpointAnnotation>()
        .Single(annotation => annotation.Name == "https");
    var deploymentNetwork = new NetworkIdentifier("deployment");
    var deployedAddress = new AllocatedEndpoint(endpoint, "web.example.test", 443,
        EndpointBindingMode.SingleAddress, networkId: deploymentNetwork);
    endpoint.AllAllocatedEndpoints.AddOrUpdateAllocatedEndpoint(deploymentNetwork, deployedAddress);
    var context = new ValueProviderContext
    {
        ExecutionContext = builder.ExecutionContext,
        Caller = web.Resource,
        Network = deploymentNetwork
    };

    string? directUrl = await web.GetEndpoint("https").GetValueAsync(context)
        .AsTask().WaitAsync(timeout);
    RequireEqual(directUrl, deployedAddress.UriString, "Normal endpoint resolution");
    Console.WriteLine($"1. Normal endpoint, deployment context: {directUrl}");

    var configuration = await GetClientConfigurationAsync(builder, web.Resource);
    Console.WriteLine($"   Generated origin provider: {configuration.ValueProviders.Single().GetType().Name}");
    Console.WriteLine($"   Generated origin expression: {configuration.ManifestExpressions.Single()}");

    using (var cancellation = new CancellationTokenSource(timeout))
    {
        try
        {
            string? json = await configuration.GetValueAsync(context, cancellation.Token)
                .AsTask().WaitAsync(cancellation.Token);
            RequireEqual(GetBrowserApiUrl(json), $"{deployedAddress.UriString}/_api/api",
                "Generated browser configuration");
            Console.WriteLine("2. Generated Blazor configuration resolved using the deployment address.");
            Console.WriteLine();
            Console.WriteLine("BUG NOT REPRODUCED: the supplied network context was respected.");
            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Console.WriteLine("2. Generated Blazor configuration, SAME context: TIMED OUT after 2000 ms.");
        }
    }

    // Allocate a different local address only after demonstrating the stalled deployment lookup.
    var localhostNetwork = KnownNetworkIdentifiers.LocalhostNetwork;
    var localAddress = new AllocatedEndpoint(endpoint, "localhost", 7443,
        EndpointBindingMode.SingleAddress, networkId: localhostNetwork);
    endpoint.AllAllocatedEndpoints.AddOrUpdateAllocatedEndpoint(localhostNetwork, localAddress);

    using var retryCancellation = new CancellationTokenSource(timeout);
    string? retryJson = await configuration.GetValueAsync(context, retryCancellation.Token)
        .AsTask().WaitAsync(retryCancellation.Token);
    string browserApiUrl = GetBrowserApiUrl(retryJson);
    RequireEqual(browserApiUrl, $"{localAddress.UriString}/_api/api",
        "Generated configuration after allocating a localhost address");
    Console.WriteLine($"3. After allocating a localhost address, SAME deployment context: {browserApiUrl}");

    directUrl = await web.GetEndpoint("https").GetValueAsync(context)
        .AsTask().WaitAsync(timeout);
    RequireEqual(directUrl, deployedAddress.UriString, "Normal endpoint control after localhost allocation");
    Console.WriteLine($"   Normal endpoint still resolves to: {directUrl}");
    Console.WriteLine();
    Console.WriteLine("BUG REPRODUCED: generated Blazor configuration used localhost instead of the supplied network.");
    Console.WriteLine("Exit code 1 is intentional when the bug is reproduced.");
    return 1;
}

static async Task<ReferenceExpression> GetClientConfigurationAsync(
    IDistributedApplicationBuilder builder, ProjectResource resource)
{
    var context = new EnvironmentCallbackContext(builder.ExecutionContext, resource);
    foreach (var annotation in resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
    {
        await annotation.Callback(context);
    }

    if (!context.EnvironmentVariables.TryGetValue("Client__ConfigResponse", out var configuration)
        || configuration is not ReferenceExpression expression
        || expression.IsConditional
        || expression.ValueProviders.Count != 1
        || expression.ManifestExpressions.Single() != "{web.bindings.https.url}")
    {
        throw new InvalidOperationException(
            "The generated Blazor configuration has changed. Review the repro before interpreting its result.");
    }

    return expression;
}

static string GetBrowserApiUrl(string? json)
{
    if (json is null)
    {
        throw new InvalidOperationException("Generated browser configuration unexpectedly resolved to null.");
    }

    using var document = JsonDocument.Parse(json);
    return document.RootElement.GetProperty("webAssembly").GetProperty("environment")
        .GetProperty("services__api__https__0").GetString()
        ?? throw new InvalidOperationException("The browser API URL unexpectedly resolved to null.");
}

static void RequireEqual(string? actual, string expected, string operation)
{
    if (!string.Equals(actual, expected, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"{operation}: expected '{expected}', received '{actual}'.");
    }
}
