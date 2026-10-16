# Aspire Blazor: generated client configuration loses the endpoint network context

Minimal, offline reproduction of a value-resolution problem in Aspire's hosted
Blazor integration. It uses the real `ProxyBlazorService` API and the resulting
`Client__ConfigResponse`; it does not reimplement or modify Aspire internals.

## Requirements and versions

- .NET 10 SDK. `global.json` selects an installed stable .NET 10 SDK.
- `Aspire.Hosting.AppHost` **13.6.1**.
- `Aspire.Hosting.Blazor` **13.6.1-preview.1.26506.6**.

The distinction between the stable core package and the preview Blazor package is
intentional: these are the versions used when the original problem was observed.
Validated on Windows with **.NET SDK 10.0.402**, targeting `net10.0`. This
reproduces without using the .NET 11 preview SDK from the original application.

No Aspire CLI, Azure subscription, Azure login, Docker, browser, web application,
database, certificates, or secrets are needed. The first restore needs access to
NuGet unless the packages are already cached. Running the repro makes no HTTP
requests and starts no distributed application or dashboard.

The pinned Aspire dependencies bring in `MessagePack` 2.5.302, which currently
produces a moderate-severity NuGet advisory
([GHSA-qhrr-8q5h-9q3h](https://github.com/advisories/GHSA-qhrr-8q5h-9q3h)).
The project leaves this warning visible but does not promote `NU1902` to an
error, so the original dependency graph can be reproduced. It does not use
MessagePack to deserialize any data. This is an isolated diagnostic, not a
production application or a recommendation to adopt that dependency version.

## Run

From this directory:

```powershell
dotnet run --project .\Repro\Repro.csproj
$LASTEXITCODE
```

The solution can also be opened in Visual Studio or VS Code:

```powershell
dotnet build .\AspireBlazorBug.slnx
```

**Exit code 1 means the bug was reproduced, not that the repro failed to run.**
Exit code 0 means the generated configuration resolved using the requested
deployment network and the bug was not reproduced. An unexpected exception is a
different failure: inspect its message and stack rather than interpreting it as
evidence of this bug.

## What the program does

Everything happens in memory in [Program.cs](Repro/Program.cs):

1. Creates an Aspire builder in publish mode with two synthetic `ProjectResource`
   objects named `web` and `api`. These are model objects, not runnable projects.
2. Calls `web.ProxyBlazorService(api)`, then executes the environment callbacks
   registered by Aspire to obtain its generated `Client__ConfigResponse`.
3. Allocates a fake HTTPS address, `https://web.example.test:443`, on a network named
   `deployment`. No address is allocated on the localhost network yet.
4. Supplies a `ValueProviderContext` explicitly selecting `deployment`.
5. Resolves the ordinary HTTPS endpoint. This must return the allocated deployment
   address immediately.
6. Resolves Aspire's generated browser configuration with the **same context**.
   On the affected package it waits. A two-second cancellation bounds the wait.
7. Allocates a different address, `https://localhost:7443`, on the localhost
   network and retries the generated configuration, still supplying `deployment`.
   On the affected package it resolves to the **localhost** address.
8. Checks that the ordinary endpoint still resolves to the deployment address.

The final control is important: the evidence is not merely "an async call took
too long." Giving localhost an address releases the blocked configuration, and
that configuration then contains the wrong network's address.

## Expected versus actual

Expected browser service-discovery URL with the supplied context:

```text
https://web.example.test:443/_api/api
```

Actual behaviour on the affected package:

```text
No localhost allocation: waits until cancelled.
After localhost allocation: https://localhost:7443/_api/api
```

The ordinary endpoint resolves correctly in both cases.

### Verified console output

After building, this command runs just the repro without repeating restore/build
warnings:

```powershell
dotnet run --project .\Repro\Repro.csproj --configuration Release --no-build
```

```text
Aspire Blazor origin-resolution repro
No host is started and no network requests are made.

1. Normal endpoint, deployment context: https://web.example.test:443
   Generated origin provider: GatewayOriginReference
   Generated origin expression: {web.bindings.https.url}
2. Generated Blazor configuration, SAME context: TIMED OUT after 2000 ms.
3. After allocating a localhost address, SAME deployment context: https://localhost:7443/_api/api
   Normal endpoint still resolves to: https://web.example.test:443

BUG REPRODUCED: generated Blazor configuration used localhost instead of the supplied network.
Exit code 1 is intentional when the bug is reproduced.
```

The fake HTTPS URL includes an explicit `:443` because that is how the allocated
endpoint formats its address. No connection is made to that hostname or port.

## Why it happens

The generated expression contains Aspire's internal `GatewayOriginReference`.
The affected implementation only implements
`GetValueAsync(CancellationToken)`. It calls the underlying endpoint with no
`ValueProviderContext`.

`IValueProvider` also exposes a context-aware overload, whose default
implementation calls the cancellation-token-only overload. Because the wrapper
does not override that overload, the context supplied by the caller is discarded.
The endpoint lookup then defaults to the localhost network.

Conceptually:

```text
Generated ReferenceExpression.GetValueAsync(deploymentContext, cancellationToken)
  -> IValueProvider's default context-aware implementation
  -> GatewayOriginReference.GetValueAsync(cancellationToken)
  -> EndpointReference.GetValueAsync(cancellationToken)
  -> localhost allocation, not deployment allocation
```

A proposed upstream correction is for `GatewayOriginReference` to implement the
context-aware overload and forward both the context and cancellation token to the
underlying endpoint, while retaining its existing trailing-slash handling.
That is a proposed correction, not an Aspire patch included in this solution.

### Package-matched source references

The package metadata identifies Aspire commit
`3751e615ad660621f2a7aa4c390ef48e08a0eed8`:

- [GatewayOriginReference](https://github.com/microsoft/aspire/blob/3751e615ad660621f2a7aa4c390ef48e08a0eed8/src/Aspire.Hosting.Blazor/GatewayOriginReference.cs).
- [GatewayConfigurationBuilder](https://github.com/microsoft/aspire/blob/3751e615ad660621f2a7aa4c390ef48e08a0eed8/src/Aspire.Hosting.Blazor/GatewayConfigurationBuilder.cs).
- [IValueProvider and its default overload](https://github.com/microsoft/aspire/blob/3751e615ad660621f2a7aa4c390ef48e08a0eed8/src/Aspire.Hosting/ApplicationModel/IValueProvider.cs).
- [EndpointReference resolution](https://github.com/microsoft/aspire/blob/3751e615ad660621f2a7aa4c390ef48e08a0eed8/src/Aspire.Hosting/ApplicationModel/EndpointReference.cs).
- [Network selection fallback](https://github.com/microsoft/aspire/blob/3751e615ad660621f2a7aa4c390ef48e08a0eed8/src/Aspire.Hosting/ApplicationModel/ValueProviderExtensions.cs).
- [Documented hosted Blazor setup](https://aspire.dev/dashboard/enable-browser-telemetry/blazor-webassembly/).

## What this proves, and what it does not

It demonstrates that Aspire's generated Blazor configuration does not respect the
network context that ordinary endpoint resolution respects. It needs no
application-specific configuration, custom domains, authentication, or Azure
resources. `ProxyBlazorService` alone is sufficient; telemetry proxying is not
needed to trigger it.

It is a focused resolver reproduction, **not** an end-to-end Azure Container Apps
deployment reproduction. It does not test browser telemetry delivery, proxy
authentication, browser startup, or the full deployment pipeline.

The original deployment symptom was a wait while resolving an input named
`<web-resource-name>_bindings_https_url`, before submitting the web Container App
deployment. This repro isolates the context-loss mechanism consistent with that
symptom without requiring a slow cloud deployment.

## Suggested upstream issue title

`Blazor GatewayOriginReference drops ValueProviderContext, causing generated client configuration to wait on localhost during publishing`

The core report is that normal endpoint resolution respects
`ValueProviderContext.Network`, but resolution through the Blazor-generated
`GatewayOriginReference` does not. The two-second timeout and the wrong-address
control can both be reproduced locally using the solution in this directory.

## Files

- [AspireBlazorBug.slnx](AspireBlazorBug.slnx): one-project solution.
- [Repro.csproj](Repro/Repro.csproj): pinned package versions and .NET 10 target.
- [Program.cs](Repro/Program.cs): the runnable reproduction and explicit controls.
- [global.json](global.json): stable .NET 10 SDK selection.

Build output is excluded by [.gitignore](.gitignore). There are no references to
the original application or machine-specific paths in the project.
