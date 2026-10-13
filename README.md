# ManagedCode.MCPGateway

[![CI](https://github.com/managedcode/MCPGateway/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/managedcode/MCPGateway/actions/workflows/ci.yml)
[![Release](https://github.com/managedcode/MCPGateway/actions/workflows/release.yml/badge.svg?branch=main)](https://github.com/managedcode/MCPGateway/actions/workflows/release.yml)
[![CodeQL](https://github.com/managedcode/MCPGateway/actions/workflows/codeql.yml/badge.svg?branch=main)](https://github.com/managedcode/MCPGateway/actions/workflows/codeql.yml)
[![NuGet](https://img.shields.io/nuget/v/ManagedCode.MCPGateway.svg?style=flat-square&logo=nuget)](https://www.nuget.org/packages/ManagedCode.MCPGateway)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Downloads](https://img.shields.io/nuget/dt/ManagedCode.MCPGateway.svg?style=flat-square&logo=nuget)](https://www.nuget.org/packages/ManagedCode.MCPGateway)

`ManagedCode.MCPGateway` is a .NET 10 library that turns local `AITool` instances and remote MCP servers into one searchable and invokable execution surface for `Microsoft.Extensions.AI`.

It is built on:

- `Microsoft.Extensions.AI`
- the official `ModelContextProtocol` .NET SDK

`ManagedCode.MCPGateway` treats the official [`modelcontextprotocol/csharp-sdk`](https://github.com/modelcontextprotocol/csharp-sdk) as its MCP protocol baseline. The package uses SDK `2.2.0` and leaves protocol negotiation to that SDK. Gateway-created and caller-provided clients remain usable at any protocol version the SDK successfully negotiates, and exported servers accept the protocol versions supported by the SDK unless the application explicitly configures one. The gateway does not apply a second version gate. The shipped gateway surface includes aggregated MCP tools, prompts, and resources; per-request subscriptions; SDK Tasks and Apps extensions; cache-aware results; and task-backed tool execution.

## Install

Install the latest stable release from [NuGet](https://www.nuget.org/packages/ManagedCode.MCPGateway):

```bash
dotnet add package ManagedCode.MCPGateway
```

## What You Get

- one gateway for local `AITool` instances and MCP tools
- one prompt catalog for source-aware MCP prompts plus gateway-owned custom and composite prompts
- one resource catalog for MCP resources and resource templates aggregated across registered MCP sources
- one downstream MCP server export path over the aggregated tool, prompt, and resource catalogs with SDK 2.2 protocol support for completions, per-request notifications, resource subscriptions, Tasks, Apps, and cache hints
- one search API with default schema-aware Markdown-LD SPARQL graph ranking, opt-in vector ranking, and vector-first `Auto`
- one graph search API for schema/profile inspection, schema-aware SPARQL search, explicit allowlisted federation, graph evidence, and graph export
- one category-first routing API for advanced tool discovery flows
- one invocation API for both local tools and MCP tools
- additive catalog registration through `IMcpGatewayRegistry`
- full in-memory catalog control through `IMcpGatewayCatalogRuntime`
- prompt inspection and rendering through `IMcpGatewayPromptCatalog`
- resource inspection and reading through `IMcpGatewayResourceCatalog`
- DI-owned factory creation for isolated custom gateway instances
- reusable gateway meta-tools for chat loops
- optional warmup, caching, query normalization, and embedding reuse
- BenchmarkDotNet performance and allocation benchmarks for search, indexing, and meta-tools

After `services.AddMcpGateway(...)`, the container exposes:

- `IMcpGateway`
- `IMcpGatewayRegistry`
- `IMcpGatewayCatalogRuntime`
- `IMcpGatewayGraphSearch`
- `IMcpGatewayPromptCatalog`
- `IMcpGatewayResourceCatalog`
- `IMcpGatewayFactory`
- `McpGatewayToolSet`

## Quickstart

```csharp
using ManagedCode.MCPGateway;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();

services.AddMcpGateway(options =>
{
    options.AddTool(
        "local",
        AIFunctionFactory.Create(
            static (string query) => $"github:{query}",
            new AIFunctionFactoryOptions
            {
                Name = "github_search_repositories",
                Description = "Search GitHub repositories by user query."
            }));

    options.AddStdioServer(
        sourceId: "filesystem",
        command: "npx",
        arguments: ["-y", "@modelcontextprotocol/server-filesystem", "/tmp"]);
});

await using var serviceProvider = services.BuildServiceProvider();
var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

var search = await gateway.SearchAsync("find github repositories");
var route = await gateway.RouteToolsAsync(new McpGatewayToolRouteRequest(
    Query: "find github repositories",
    MaxCategories: 3,
    MaxToolsPerCategory: 2));
var invoke = await gateway.InvokeAsync(new McpGatewayInvokeRequest(
    ToolId: search.Matches[0].ToolId,
    Query: "managedcode"));
```

Default behavior:

- `SearchStrategy = Graph`
- `MarkdownLdGraphSource = GeneratedToolGraph`
- `MarkdownLdGraphSearchMode = Hybrid`
- `SearchQueryNormalization = TranslateToEnglishWhenAvailable`
- `DefaultSearchLimit = 5`
- `MaxSearchResults = 50`
- `MaxDescriptorLength = 16384`
- the index is built lazily on first list, search, or invoke

`Hybrid` means the Markdown-LD graph path runs schema-aware SPARQL search first, then uses the gateway-built ranked graph candidate path as supporting evidence and fuzzy fallback for noisy queries. It is not a tokenizer-only search path.

`DefaultSearchLimit` is the normal top-N result size when a caller does not ask for a count. `MaxSearchResults` is only a hard cap for caller-requested result sizes so LLM-facing tool discovery cannot accidentally flood context. `MaxDescriptorLength` bounds the generated descriptor text used by search and Markdown-LD graph indexing; hosts with very large tool schemas can raise it explicitly.

## Register Tools And Sources

Register local tools during startup:

```csharp
services.AddMcpGateway(options =>
{
    options.AddTool(
        "local",
        AIFunctionFactory.Create(
            static (string query) => $"weather:{query}",
            new AIFunctionFactoryOptions
            {
                Name = "weather_search_forecast",
                Description = "Search weather forecast and temperature information by city name."
            }));
});
```

Register MCP sources during startup:

```csharp
services.AddMcpGateway(options =>
{
    options.AddHttpServer(
        sourceId: "docs",
        endpoint: new Uri("https://example.com/mcp"));

    options.AddStdioServer(
        sourceId: "filesystem",
        command: "npx",
        arguments: ["-y", "@modelcontextprotocol/server-filesystem", "/tmp"]);
});
```

`AddHttpServer(...)` uses the official MCP C# SDK Streamable HTTP transport and SDK-owned protocol negotiation, and keeps the source registered as an HTTP MCP source in gateway descriptors and downstream export metadata. `McpGatewayHttpServerOptions` exposes additional headers, connection timeout, and OAuth configuration. The package does not set a transport timeout by default; hosts can pass one explicitly or own deadline policy through cancellation tokens and hosting infrastructure.

`AddStdioServer(...)` inherits the host process environment by default, matching the official SDK. For a third-party or otherwise untrusted stdio server, use `McpGatewayStdioServerOptions` to disable inheritance and pass the SDK's curated startup environment explicitly:

```csharp
using ManagedCode.MCPGateway;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;

var safeEnvironment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();

services.AddMcpGateway(options =>
{
    options.AddStdioServer(new McpGatewayStdioServerOptions
    {
        SourceId = "filesystem",
        Command = "npx",
        Arguments = ["-y", "@modelcontextprotocol/server-filesystem", "/tmp"],
        InheritEnvironmentVariables = false,
        EnvironmentVariables = safeEnvironment,
        ShutdownTimeout = TimeSpan.FromSeconds(10)
    });
});
```

The stdio options object also exposes the SDK shutdown timeout and stderr callback. Add any server-specific variables that are not in the curated environment before registration.

You can also register:

- existing `McpClient` instances through `AddMcpClient(...)`
- deferred `McpClient` factories through `AddMcpClientFactory(...)`

When a host already has SDK MCP server handlers and needs a local client for a gateway catalog, `McpInMemorySession.CreateAsync(options, services, loggerFactory, cancellationToken)` runs both ends in process. Add `session.Client` through `AddMcpClient(..., disposeClient: false)` and dispose the session after the gateway instance. The host owns its handler and authorization configuration.

If you need to add tools or sources after the container is built, use `IMcpGatewayRegistry`:

```csharp
await using var serviceProvider = services.BuildServiceProvider();

var registry = serviceProvider.GetRequiredService<IMcpGatewayRegistry>();
var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

registry.AddTool(
    "runtime",
    AIFunctionFactory.Create(
        static (string query) => $"status:{query}",
        new AIFunctionFactoryOptions
        {
            Name = "project_status_lookup",
            Description = "Look up project status by identifier or short title."
        }));

var tools = await gateway.ListToolsAsync();
```

Registry updates invalidate the catalog. The next list, search, or invoke rebuilds the index automatically.

## List And Render MCP Prompts

If registered MCP sources expose prompts, resolve `IMcpGatewayPromptCatalog` from DI and use it as the aggregated MCP prompt surface:

```csharp
var promptCatalog = serviceProvider.GetRequiredService<IMcpGatewayPromptCatalog>();

var prompts = await promptCatalog.ListPromptsAsync();
var prompt = await promptCatalog.GetPromptAsync(new McpGatewayPromptRequest(
    SourceId: prompts[0].SourceId,
    PromptName: prompts[0].PromptName,
    Arguments: new Dictionary<string, object?>
    {
        ["repository"] = "ManagedCode.MCPGateway"
    }));
```

Prompt retrieval is source-aware on purpose. Different MCP servers may expose prompts with the same name, so callers should use the `SourceId` plus `PromptName` pair returned by `ListPromptsAsync()`. Identically named prompts are not merged implicitly by the gateway. If you want one higher-level prompt that combines or modifies multiple upstream prompts, register an explicit gateway-owned prompt instead.

## Create Gateway-Owned Prompts

You can register custom prompts directly on the gateway and compose them from multiple upstream prompt sources:

```csharp
services.AddMcpGateway(options =>
{
    options.AddMcpClient("repo", repositoryClient, disposeClient: false);
    options.AddMcpClient("ops", operationsClient, disposeClient: false);

    options.AddPrompt(
        new McpGatewayPrompt("release_review_bundle", async (context, cancellationToken) =>
        {
            var repositoryPrompt = await context.GetPromptAsync(
                "repo",
                "repository_triage_system_prompt",
                new Dictionary<string, object?>
                {
                    ["repository"] = context.Arguments["repository"],
                    ["locale"] = context.Arguments["locale"]
                },
                cancellationToken);

            var deploymentPrompt = await context.GetPromptAsync(
                "ops",
                "deployment_review_system_prompt",
                new Dictionary<string, object?>
                {
                    ["environment"] = context.Arguments["environment"]
                },
                cancellationToken);

            return new GetPromptResult
            {
                Description = "Release review bundle prompt.",
                Messages =
                [
                    new PromptMessage
                    {
                        Role = Role.User,
                        Content = new TextContentBlock
                        {
                            Text = "Combine repository and deployment guidance into one review plan."
                        }
                    },
                    ..repositoryPrompt!.Messages,
                    ..deploymentPrompt!.Messages
                ]
            };
        })
        {
            DisplayName = "Release review bundle",
            Description = "Combines repository and deployment review guidance into one prompt.",
            Arguments =
            [
                new McpGatewayPromptArgumentDescriptor("repository", "Repository", "Repository name.", true),
                new McpGatewayPromptArgumentDescriptor("environment", "Environment", "Deployment environment.", true),
                new McpGatewayPromptArgumentDescriptor("locale", "Locale", "Preferred locale.", false)
            ],
            CompleteAsync = static (context, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                var values = context.ArgumentName == "repository"
                    ? new[] { "ManagedCode/MCPGateway", "ManagedCode/AIBase" }
                    : Array.Empty<string>();

                var matches = values
                    .Where(value => value.StartsWith(context.ArgumentValue, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                return ValueTask.FromResult<CompleteResult?>(new CompleteResult
                {
                    Completion = new Completion
                    {
                        Values = matches,
                        Total = matches.Count,
                        HasMore = false
                    }
                });
            }
        });
});
```

Use gateway-owned prompts when you need:

- one prompt that combines several upstream prompts
- a modified or opinionated overlay on top of an upstream prompt
- custom prompt argument completion values exposed through downstream MCP `completion/complete`

## List And Read MCP Resources

If registered MCP sources expose resources, resolve `IMcpGatewayResourceCatalog` from DI and use it as the aggregated MCP resource surface:

```csharp
var resourceCatalog = serviceProvider.GetRequiredService<IMcpGatewayResourceCatalog>();

var resources = await resourceCatalog.ListResourcesAsync();
var templates = await resourceCatalog.ListResourceTemplatesAsync();
var issue = await resourceCatalog.ReadResourceAsync(new McpGatewayResourceRequest(
    SourceId: templates[0].SourceId,
    ResourceUri: "docs://issues/42"));
```

Resource reads are source-aware on purpose. Different MCP servers may expose the same URI scheme or URI shape, so callers should use the `SourceId` returned by `ListResourcesAsync()` or `ListResourceTemplatesAsync()`. For templated resources, expand the template to a concrete resource URI before calling `ReadResourceAsync(...)`.

## Export The Gateway As An MCP Server

If you want one downstream MCP endpoint over the aggregated gateway catalog, register an MCP server and add the gateway export:

```csharp
var services = new ServiceCollection();
services.AddLogging();
services.AddMcpGateway(options =>
{
    options.AddMcpClient("docs", docsClient, disposeClient: false);
    options.AddMcpClient("ops", opsClient, disposeClient: false);
});

services.AddMcpServer()
    .WithStdioServerTransport()
    .WithMcpGatewayCatalog();
```

`WithMcpGatewayCatalog()` exports:

- aggregated tools through MCP `tools/list` and `tools/call`
- aggregated prompts through MCP `prompts/list` and `prompts/get`
- aggregated resources through MCP `resources/list`, `resources/templates/list`, and `resources/read`
- prompt and resource completions through MCP `completion/complete`
- protocol `2026-07-28` discovery through SDK `server/discover`, per-request metadata, `resultType`, `ttlMs`, and `cacheScope`
- per-request `subscriptions/listen` for prompt list changes and resource updates, including subscription-id tagging and deterministic upstream cleanup
- task-backed tool execution through the `io.modelcontextprotocol/tasks` extension with `tasks/get`, `tasks/update`, and `tasks/cancel`
- MCP Apps through `io.modelcontextprotocol/ui`, including transparent tool UI metadata and source-aware UI resource routing

Exported MCP tool names are canonical lowercase MCP-safe gateway ids. A unique upstream tool such as `notion-fetch` is exported as `notion-fetch`; when multiple sources expose the same normalized tool name, the gateway uses a source-qualified safe id such as `docs_search_repository` or `ops_search_repository`. Hash suffixes are added only when length or collision handling requires them. Names that would exceed 64 characters keep a readable normalized prefix and receive a deterministic hash suffix instead of being blindly truncated. `McpGatewayToolDescriptor.ToolId`, downstream MCP `Tool.Name`, and downstream `tools/call` names are the same value, and gateway-owned name resolution is case-insensitive so callers can vary casing without creating a different tool identity. Prompt names and exported resource names use the same lowercase MCP-safe source qualification, for example `ops_deployment_review_system_prompt` or `local_release_review_bundle`, while raw upstream `SourceId`, prompt name, tool name, and resource URI remain available as routing metadata and `_meta` values. Exported MCP resource URIs and URI templates are rewritten into gateway-owned opaque URIs so downstream `resources/read` calls route back to the correct upstream source even when multiple servers expose overlapping URI spaces. The same source-aware rewrite is used for MCP App `_meta.ui.resourceUri`, `completion/complete`, forwarded prompt list changes, and forwarded resource update notifications. Output schemas are preserved as arbitrary JSON Schema 2020-12 values, including boolean, primitive, and array schemas rather than only object schemas.

The Tasks extension uses `tasks/get`, `tasks/update`, and `tasks/cancel`. A client opts into task execution through its extension capability. The gateway first invokes an upstream tool normally and, when the upstream server requires Tasks, retries through the SDK polling flow. Local and upstream tools can also run as gateway-owned downstream tasks.

The default exported task store wraps the official SDK `InMemoryMcpTaskStore` with bounded defaults: task TTL 30 minutes, polling interval 1 second, maximum 10,000 retained tasks, and at most 60 consecutive unchanged upstream polls. Hosts can override those values through `McpGatewayOptions.McpTaskStore`:

```csharp
services.AddMcpGateway(options =>
{
    options.McpTaskStore.TaskTimeToLive = TimeSpan.FromHours(1);
    options.McpTaskStore.PollInterval = TimeSpan.FromMilliseconds(500);
    options.McpTaskStore.MaximumTasks = 50_000;
    options.McpTaskStore.MaximumConsecutiveStuckPolls = 120;
});
```

For a caller-owned task store, pass any `IMcpTaskStore` implementation directly to the export registration. The following uses the SDK store explicitly; durable or distributed hosts can supply their own implementation through the same overload:

```csharp
using ModelContextProtocol.Extensions.Tasks;

var taskStore = new InMemoryMcpTaskStore
{
    DefaultTimeToLive = TimeSpan.FromHours(1),
    DefaultPollIntervalMs = 500
};

services.AddMcpServer()
    .WithStdioServerTransport()
    .WithMcpGatewayCatalog(taskStore);
```

`WithMcpGatewayCatalog()` enforces `HttpServerTransportOptions.Stateless = true` for Streamable HTTP. HTTP supports discovery, list, read, call, completion, and task requests; `subscriptions/listen` is acknowledged without notification grants because HTTP does not retain listener state. Use stdio for current-protocol prompt and resource notifications.

Custom `IMcpGatewayServerSource` adapters expose current listener lifetimes through `ListenForPromptListChangesAsync(...)` and `ListenForResourceUpdatesAsync(...)`. These methods feed `subscriptions/listen` coordination and are disposed when the downstream listener is cancelled; they do not expose down-level resource RPCs.

If the downstream MCP host cannot use the default singleton `IMcpGateway`, `IMcpGatewayPromptCatalog`, and `IMcpGatewayResourceCatalog` registrations directly, register a custom `IMcpGatewayServerBindingResolver`. The resolver can create or select a request-specific gateway instance and return it through `McpGatewayServerBinding`, while `WithMcpGatewayCatalog()` continues to own the exported MCP handlers, prompt/resource notifications, subscriptions, and task flow:

```csharp
services.AddMcpGateway();

services.AddSingleton<IMcpGatewayServerBindingResolver>(serviceProvider =>
    new RouteScopedGatewayBindingResolver(
        serviceProvider.GetRequiredService<IMcpGatewayFactory>()));

services.AddMcpServer()
    .WithHttpTransport()
    .WithMcpGatewayCatalog();
```

Use the default resolver when one singleton aggregated gateway is enough. Use a custom binding resolver when the exported MCP endpoint needs to select a different gateway instance per downstream route, tenant, or authenticated request.

If you need to fully reconfigure the in-memory runtime catalog, use `IMcpGatewayCatalogRuntime` instead of internal reflection:

```csharp
var catalogRuntime = serviceProvider.GetRequiredService<IMcpGatewayCatalogRuntime>();
var replacement = new McpGatewayOptions()
    .AddTool(
        "runtime",
        AIFunctionFactory.Create(
            static (string query) => $"status:{query}",
            new AIFunctionFactoryOptions
            {
                Name = "project_status_lookup",
                Description = "Look up project status by identifier or short title."
            }));

await catalogRuntime.ReconfigureAsync(replacement);
```

## Factory-Created Custom Gateways

If you want an isolated custom gateway instance beyond the default singleton gateway, resolve `IMcpGatewayFactory` from DI and create a custom gateway from there:

```csharp
var services = new ServiceCollection();
services.AddLogging();
services.AddMcpGateway();

await using var serviceProvider = services.BuildServiceProvider();
var factory = serviceProvider.GetRequiredService<IMcpGatewayFactory>();

await using var gatewayHost = factory.Create(options =>
{
    options.AddTool(
        "local",
        AIFunctionFactory.Create(
            static (string query) => $"github:{query}",
            new AIFunctionFactoryOptions
            {
                Name = "github_search_repositories",
                Description = "Search GitHub repositories by user query."
            }));
});

var search = await gatewayHost.Gateway.SearchAsync("find github repositories");
var prompts = await gatewayHost.PromptCatalog.ListPromptsAsync();
```

Use the package surfaces like this:

- `IMcpGateway`: build, list, search, route, invoke
- `IMcpGatewayRegistry`: additive tool and source registration
- `IMcpGatewayCatalogRuntime`: full in-memory catalog clear or reconfiguration
- `IMcpGatewayGraphSearch`: schema/profile inspection, schema-aware SPARQL graph search, explicit federation, and graph export
- `IMcpGatewayPromptCatalog`: list and render aggregated upstream plus gateway-owned prompts
- `IMcpGatewayResourceCatalog`: list direct resources, list resource templates, and read concrete resource URIs
- `IMcpGatewayFactory`: create isolated custom gateway instances
- `McpGatewayToolSet`: reusable meta-tools

## Search And Invoke

Search first, then invoke by `ToolId`:

```csharp
var search = await gateway.SearchAsync("find github repositories");

var invoke = await gateway.InvokeAsync(new McpGatewayInvokeRequest(
    ToolId: search.Matches[0].ToolId,
    Query: "managedcode"));
```

If the caller already knows the exact tool identity, invocation can use `ToolName` plus `SourceId`:

```csharp
var invoke = await gateway.InvokeAsync(new McpGatewayInvokeRequest(
    ToolName: "github_search_repositories",
    SourceId: "local",
    Query: "managedcode"));
```

For contextual search and invocation, pass `ContextSummary` and `Context`:

```csharp
var search = await gateway.SearchAsync(new McpGatewaySearchRequest(
    Query: "search",
    ContextSummary: "User is on the GitHub repository settings page",
    Context: new Dictionary<string, object?>
    {
        ["page"] = "settings",
        ["domain"] = "github"
    },
    MaxResults: 3));

var invoke = await gateway.InvokeAsync(new McpGatewayInvokeRequest(
    ToolId: search.Matches[0].ToolId,
    Query: "managedcode",
    ContextSummary: "User wants repository administration actions",
    Context: new Dictionary<string, object?>
    {
        ["page"] = "settings",
        ["domain"] = "github"
    }));
```

`McpGatewaySearchResult` returns:

- `Matches` for the primary result set
- `RelatedMatches` for bounded graph-related expansion
- `NextStepMatches` for bounded graph next-step expansion
- `Diagnostics` for fallback, normalization, and retrieval signals
- `RankingMode` with `graph`, `vector`, `hybrid`, `browse`, or `empty`
- `UsedSchemaSearch` and `UsedSchemaFallback` for the Markdown-LD schema/SPARQL path
- `FocusedGraphNodeCount` and `FocusedGraphEdgeCount` for focused graph scope

`McpGatewayInvokeResult` returns:

- `IsSuccess`
- `ToolId`
- `SourceId`
- `ToolName`
- `Output`
- `Error`
- `McpResult`: the approved complete `CallToolResult` for MCP sources and local AIFunctions that return the official protocol result, preserving content blocks, structured content, metadata, and `IsError`. `Output` remains the normalized convenience view. Output schemas validate structured content rather than the protocol envelope. A completed protocol call can contain a tool-level error; protocol bridges forward `McpResult`. Result policies, projection and delivery bounds still apply before publication.

## Governed Publication And Progressive Discovery

Enable managed mode when technical registration must remain separate from reviewed publication. Register your host implementations of `IMcpGatewayExecutionContextProvider` and `IMcpGatewayAccessPolicy`; identity comes from authenticated host state, never from tool arguments or `Context`. The access policy covers discovery, schemas, dispatch, delivery, prompts, resources, notifications, graph administration/federation, tasks and result reads.

```csharp
services.AddScoped<IMcpGatewayExecutionContextProvider, RequestIdentityProvider>();
services.AddScoped<IMcpGatewayAccessPolicy, GatewayAccessPolicy>();
services.AddMcpGatewayInMemoryCatalogStore();
services.AddMcpGatewayInMemoryResultStore();
services.AddMcpGateway(options =>
{
    options.Governance.ManagedCatalog = true;
    options.Governance.MaximumResponseBytes = 64 * 1024;
    options.MaximumToolSchemaBytes = 64 * 1024;
    options.AddTool("weather", AIFunctionFactory.Create(WeatherTools.GetForecast));
});
```

`RequestIdentityProvider` and `GatewayAccessPolicy` above are application services you supply. Managed tools remain unavailable until a trusted reviewer publishes definitions. `IMcpGatewayCatalogPublication.InspectCandidateAsync()` returns immutable candidate definitions; apply reviewed permissions/provenance with `WithReviewMetadata`, set approved lifecycle, then call `PublishAsync` with the expected revision and reviewer reference. Conflicts, schema drift and failed validation retain the previous published catalog. `RevokeAsync` immediately gates future dispatch/delivery, including previously materialized proxies and stored results. Distributed hosts can replace `IMcpGatewayCatalogDefinitionStore` with their own atomic implementation.

Use compact discovery before requesting a complete schema:

```csharp
var discovery = serviceProvider.GetRequiredService<IMcpGatewayDiscovery>();
var page = await discovery.DiscoverAsync(new("weather forecast")
{
    MaxResults = 5,
    MaxBytes = 2048,
});
var schema = await discovery.GetToolSchemaAsync(page.Tools[0].ToolId);
```

Summaries contain purpose, ID, source, hints, score, schema hash and catalog revision. Full schemas are omitted. A continuation token is bound to the visible selection/query/revision and the entire serialized page respects the byte budget. Exact schema lookup preserves nested constraints and returns a typed `McpGatewaySchemaTooLargeException` when its complete response exceeds `MaximumToolSchemaBytes` (64 KiB default); increase that host option when a large schema is intentional. No schema or JSON response is truncated.

`McpGatewayDiscoveryToolSet.CreateSchemaFaithfulToolsAsync` creates proxies with the actual tool argument schema. SDK hosts use `services.AddMcpServer().WithMcpGatewayDiscovery()` for the fixed compact discovery, schema and invocation tool set. A registered result store adds the result-reading tool. `WithMcpGatewayCatalog()` retains the existing complete catalog export. Factory-created instances expose their own `Discovery`, `Publication` and optional `ResultReader` services and use their configured execution/response limits.

## Approved Results And Bounded Execution

```csharp
var result = await gateway.InvokeAsync(new(ToolId: "weather_forecast")
{
    ResponseOptions = new()
    {
        SelectPaths = ["/location", "/days/0/temperature"],
        MaxBytes = 2048,
        StoreOversizedResult = true,
    },
});
```

Response options stay inside the gateway. Strict JSON Pointer projection preserves selected JSON values and array positions. `IMcpGatewayResultPolicy` can approve/redact output before schema validation, projection or storage; its approved JSON replaces raw MCP text, structured content and metadata. Outcomes distinguish tool business errors, invalid arguments, denied access, capacity rejection, cancellation, timeout, uncertain mutation completion and rejected delivery. A mutating call that times out after dispatch is never automatically retried.

The optional result store defaults to 256 entries, 16 MiB total, 4 MiB per result and a 15-minute lifetime. It uses host-owned `IMemoryCache`; handles require the originating actor, tenant, authorization partition and policy revision plus live publication/access checks. `IMcpGatewayResultReader.ReadAsync` accepts the same projection/budget options. Reader access cannot widen the originating managed instance's response cap. Supply `IMcpGatewayResultStore` for persistent storage; managed implementations must preserve definition and delivery-limit metadata.

Managed mode enables bounded execution; other hosts opt in with `options.Execution.Enabled = true`. A trusted context deadline is enforced independently of admission mode; model context cannot extend it. Defaults are 32 concurrent calls and 64 queued calls per source, 30-second queue wait, 60-second execution deadline, and a 30-second circuit break after five transport failures. Configure `options.Execution.Sources` for specific limits and future source IDs; the default controller set is finite. Dynamic/distributed hosts can supply `IMcpGatewaySourceAdmission`. Work that ignores cancellation retains its capacity/client/catalog lease until actual completion.

For remote tools, configure `options.Execution.RemoteBindings` and implement `IMcpGatewayExecutionClientResolver` to return a caller-bound official SDK client lease. The gateway checks binding reference, audience, credential revision, scopes and current upstream schema before dispatch. Shared technical execution requires explicit `UseSharedTechnicalClient` approval. Managed HTTP imports and remote federation require `McpGatewayEgressHttpClient.Create` with your `IMcpGatewayEgressPolicy`, trusted context provider and `SocketsHttpHandler`; destination and resolved IP approval happen before connection, and redirects cannot forward credentials implicitly. Optional context forwarding uses `RestrictForwardedContext`, `ForwardedContextKeys` and `ForwardContextSummary`; managed mode always restricts it.

Configure periodic refresh only for explicitly selected sources:

```csharp
services.AddMcpGatewaySourceRefresh(options =>
{
    options.SourceIds.Add("weather");
    options.Interval = TimeSpan.FromMinutes(5);
    options.MaxConcurrency = 4;
});
```

`IMcpGatewayCatalogRuntime.RefreshSourceAsync` and official SDK list-change notifications use the same targeted single-flight refresh. `SourceRefreshTimeout` and `SourceNotificationSubscriptionTimeout` default to 30 seconds and can be overridden. Failed refresh preserves the last validated catalog; unchanged sources retain their clients, and retired clients wait for active operations. Hosted refresh uses configurable bounded backoff and stops with the host.

## Search Hints

If a tool should be easier to find through multilingual aliases, stable domain keywords, category-first routing, or execution-aware discovery, register explicit search hints:

```csharp
services.AddMcpGateway(options =>
{
    options.AddTool(
        AIFunctionFactory.Create(
            static () => "ok",
            new AIFunctionFactoryOptions
            {
                Name = "notification_activity_search",
                Description = "List notification inbox alerts, unread activity, mentions, and message updates."
            }),
        new McpGatewayToolSearchHints(
            Aliases:
            [
                "сповіщення",
                "нотифікації",
                "уведомления"
            ],
            Keywords:
            [
                "alerts",
                "inbox",
                "mentions"
            ],
            Categories: ["communications"],
            Tags: ["notifications", "activity-feed"],
            DataSources: ["inbox-api"],
            UsageExamples:
            [
                new McpGatewayToolExample(
                    "show unread alerts",
                    "{\"count\":3}",
                    "Check whether the user has pending notifications."),
                new McpGatewayToolExample(
                    "summarize mentions from the last release thread",
                    "{\"mentions\":[\"alice\",\"bob\"]}",
                    "Triage release-thread mentions before replying."),
                new McpGatewayToolExample(
                    "list urgent notifications for the mobile workspace",
                    "{\"alerts\":[{\"severity\":\"high\"}]}",
                    "Filter alerts for a specific workspace and urgency level.")
            ],
            ReadOnly: true,
            Idempotent: true,
            CostTier: McpGatewayToolCostTier.Low,
            LatencyTier: McpGatewayToolLatencyTier.Low));
});
```

Hints are included in descriptors, Markdown-LD graph documents, vector documents, exported MCP metadata, and lexical boosts. They are the preferred way to improve multilingual discovery and category-aware routing without hardcoded ranking exceptions.

If a tool should stay out of default discovery until a host explicitly opts into it, set `EnabledByDefault: false`. Standard `SearchAsync(...)` and `RouteToolsAsync(...)` hide those tools unless the request sets `IncludeDisabledTools = true`.

## Search Strategies

### Graph

`Graph` is the default and does not require embeddings:

```csharp
services.AddMcpGateway(options =>
{
    options.SearchStrategy = McpGatewaySearchStrategy.Graph;
});
```

Use it when you want deterministic Markdown-LD graph retrieval with related and next-step expansion. The default graph mode is schema-aware `Hybrid`: it asks `ManagedCode.MarkdownLd.Kb` to generate and execute schema-scoped SPARQL against the tool graph, then merges gateway-ranked graph candidate results as supporting evidence. If schema search finds no mapped gateway tools, hybrid mode enables fuzzy token matching in that candidate fallback so typo-heavy queries such as `trak shipmnt` can still map to shipment-tracking tools without embeddings. Large catalogs bind a bounded candidate subject set through `VALUES` on the existing graph. A caller-supplied schema profile `CandidateNodeIds` is preserved as the explicit candidate pool; null enables normal gateway selection, while an empty collection matches no primary subjects. An explicit pool keeps schema results authoritative in Hybrid mode too, so secondary ranking or fuzzy fallback cannot introduce primary tools outside that pool. This retrieval filter is not an authorization boundary. The graph engine deduplicates repeated catalog documents, symmetric related edges, topics, entities, and assertions while keeping confidence values bounded, so repeated file/custom-document inputs do not inflate the graph or exported JSON-LD.

```csharp
services.AddMcpGateway(options =>
{
    options.SearchStrategy = McpGatewaySearchStrategy.Graph;
    options.UseHybridMarkdownLdGraphSearch();
});
```

Exact tool IDs and names are resolved before schema-query normalization for non-federated graph search. Their tool nodes bind subject `VALUES` in real schema-aware SPARQL on the existing graph, so shared prefixes and action-word filtering cannot displace a known tool with a sibling operation. Relationship evidence and related/next-step expansion use the same complete graph; search does not reconstruct a candidate graph.

When a configured `MarkdownLdGraphSchemaSearchProfile` supplies candidate IDs, type filters, excluded types, or facets, its SPARQL primary result is authoritative in Hybrid mode. Ranked merging and fuzzy fallback cannot restore tools rejected by those constraints. Related and next-step expansion still follows the configured graph relationships. These retrieval constraints do not replace host authorization.

If a host wants to force only the schema-aware SPARQL path or explicitly use the older lower-level token-distance graph path:

```csharp
services.AddMcpGateway(options =>
{
    options.UseSchemaAwareMarkdownLdGraphSearch();
});

services.AddMcpGateway(options =>
{
    options.UseTokenDistanceMarkdownLdGraphSearch();
});
```

### Embeddings

`Embeddings` uses vector ranking first:

```csharp
services.AddMcpGateway(options =>
{
    options.SearchStrategy = McpGatewaySearchStrategy.Embeddings;
});
```

Use it when the host provides an embedding generator and wants purely embedding-first ranking.

### Auto

`Auto` runs vector ranking first and then uses the Markdown-LD graph as a bounded supplement:

```csharp
services.AddMcpGateway(options =>
{
    options.SearchStrategy = McpGatewaySearchStrategy.Auto;
});
```

Use it when you want semantic ranking without losing graph-based related and next-step expansion. For larger catalogs, `Auto` avoids unbounded graph supplementation after a usable vector primary result; graph fallback still runs when vector ranking is unavailable or unusable.

If vector ranking is unavailable or unusable, `Auto` falls back to graph ranking and reports diagnostics.

## Schema-Aware SPARQL Graph Search

Use `IMcpGatewayGraphSearch` when the caller needs graph schema/profile inspection, graph evidence, generated SPARQL, focused graph counts, federation metadata, or graph exports:

```csharp
var graphSearch = serviceProvider.GetRequiredService<IMcpGatewayGraphSearch>();

var schema = await graphSearch.DescribeGraphSchemaAsync();
Console.WriteLine(schema.Prefixes["schema"]);
Console.WriteLine(schema.GraphNodeCount);

var graphResult = await graphSearch.SearchGraphAsync(
    new McpGatewayGraphSearchRequest("severity filter")
    {
        MaxResults = 3
    });

Console.WriteLine(graphResult.GeneratedSparql);
Console.WriteLine(graphResult.Matches[0].ToolMatch?.ToolId);
```

`McpGatewayGraphSchemaResult` returns:

- graph availability, node count, edge count, and graph source
- search strategy, graph search mode, default limits, and max result settings
- schema prefixes, text predicates, relationship predicates and their predicate paths, expansion predicates, type filters, excluded types, and facets
- the configured candidate pool, preserving `null` for no caller pool and an empty list for no primary candidates; the default tool profile may still select candidates per query
- configured federated service endpoints
- diagnostics when the profile cannot be validated against the current graph

`McpGatewayGraphSearchResult` returns:

- `Matches`, `RelatedMatches`, and `NextStepMatches`
- `GeneratedSparql` and `GeneratedExpansionSparql`
- graph evidence with predicate ids, matched text, source context, and optional service endpoint
- mapped gateway `ToolMatch` values when a graph node maps back to a registered tool
- focused graph node and edge counts

Federated graph search is explicit. Configure allowed SPARQL service endpoints first:

```csharp
services.AddMcpGateway(options =>
{
    options.AddMarkdownLdFederatedServiceEndpoint(
        new Uri("https://knowledge.example.com/sparql"));

    options.MarkdownLdFederatedSparqlQueryTimeout = TimeSpan.FromSeconds(30);
});
```

Then request federation:

```csharp
var federatedResult = await graphSearch.SearchGraphAsync(
    new McpGatewayGraphSearchRequest("story detail lookup")
    {
        UseFederation = true,
        IncludeLocalGatewayGraph = true,
        ServiceEndpoints = ["https://knowledge.example.com/sparql"]
    });
```

The gateway never discovers remote SPARQL endpoints on its own. It uses the configured allowlist, can bind the local gateway graph as a federated service, and reports diagnostics when a requested endpoint is invalid or blocked.
Allowlist matching preserves URI path and query casing after .NET URI normalization. If local federation is disabled and no allowed endpoint is selected, the result is empty with `graph_federation_no_endpoints`; the gateway does not execute a local search.
Federated SPARQL execution defaults to `McpGatewayOptions.DefaultMarkdownLdFederatedSparqlQueryTimeout`, which is 30 seconds. Set `MarkdownLdFederatedSparqlQueryTimeout` to a larger `TimeSpan` for slower trusted endpoints, or set it to `null` when the host owns the deadline entirely through cancellation tokens or infrastructure policy.

The default tool profile uses a bounded tool candidate pool for large catalogs. An application-owned profile with no explicit candidate pool searches the complete graph, so ontology classes and properties remain searchable even in large catalogs. For a gateway instance dedicated to vocabulary exploration, set `options.MarkdownLdGraphSchemaSearchProfile = KnowledgeGraphSchemaSearchProfile.Ontology` and query it through `IMcpGatewayGraphSearch.SearchGraphAsync(...)`. Ontology matches that have no registered tool binding return `ToolMatch = null`.

Lucene full-text indexing/search and SPIN packages are available in `ManagedCode.MarkdownLd.Kb`. The gateway currently exposes schema-aware SPARQL, ranked/token support and explicit federation; it has no dedicated Lucene or SPIN execution API.

## Lucene Full-Text And Caller-Bound Federation

`IMcpGatewayGraphFullTextSearch.BuildFullTextIndexAsync()` builds or inspects the Lucene index for the current authorized RDF graph. `SearchFullTextAsync(query, maxResults)` returns node identity, literal label, raw Lucene relevance and mapped tool ID. `McpGatewayFullTextToolSet.CreateTools()` exposes `gateway_graph_fulltext_build` and `gateway_graph_fulltext_search`; Lucene scores are separate from calibrated gateway confidence. Reconfiguration retires old indexes after active readers drain.

For managed remote SPARQL, configure `McpGatewayFederatedServiceBinding` entries in `options.FederatedServiceBindings`, explicitly allow their endpoints, and register `IMcpGatewayFederatedClientResolver`. Each lease supplies a caller-owned egress HttpClient, binding reference, audience, credential revision and scopes. The gateway authorizes the actual query/endpoints before dispatch, validates those claims and keeps leases until the official dotNetRDF SERVICE request finishes. No default search performs unconfigured network calls.

Application-owned graphs register `IMcpGatewayAuthorizedGraphFactory` to return an independently owned authorized projection. `KnowledgeGraph.Project` in `ManagedCode.MarkdownLd.Kb 0.2.16` accepts explicit allowed subjects, resource objects and predicates; it preserves RDF identity without following private neighbors automatically. Ontology profiles continue through the schema/SPARQL APIs. The official SPIN package is present, but usable RDF-command/offline execution is blocked by [dotNetRDF issue 911](https://github.com/dotnetrdf/dotnetrdf/issues/911); the gateway exposes no fake SPIN executor.

## Graph Sources

By default the gateway generates Markdown-LD tool documents from the current catalog during index build:

```csharp
services.AddMcpGateway(options =>
{
    options.SearchStrategy = McpGatewaySearchStrategy.Graph;
    options.UseGeneratedMarkdownLdGraph();
});
```

You can also point the runtime at an exported JSON-LD graph (`.jsonld`), a previously written graph bundle (`.json`), a Markdown-LD file, or a directory:

```csharp
services.AddMcpGateway(options =>
{
    options.SearchStrategy = McpGatewaySearchStrategy.Graph;
    // Exported JSON-LD / RDF graph:
    options.UseMarkdownLdGraphFile("artifacts/mcp-tools.graph.jsonld");

    // Alternatively, the supported JSON bundle of Markdown-LD source documents:
    // options.UseMarkdownLdGraphFile("artifacts/mcp-tools.graph.json");
});
```

Both file formats remain supported. The `.json` bundle stores source documents and builds their graph during initialization; `.jsonld` loads the exported graph directly. Select one source per gateway instance.

For a JSON-LD graph embedded as an assembly resource, load it directly without extracting a filesystem copy:

```csharp
options.UseJsonLdGraphResource(typeof(Program).Assembly, "MyApp.tools.graph.jsonld");
```

If the resource owns its canonical node URIs, bind each registered tool explicitly without rewriting the graph:

```csharp
options.UseJsonLdGraphResource(
    typeof(Program).Assembly,
    "MyApp.tools.graph.jsonld",
    tool => new Uri($"https://catalog.example.com/tools/{Uri.EscapeDataString(tool.ToolName)}/"));
```

Bindings must be absolute, unique URIs present in the resource. Invalid or missing bindings produce graph-index diagnostics; the original graph is not regenerated. Selecting another graph source clears this binding configuration.

For a graph with its own vocabulary, configure `MarkdownLdGraphSchemaSearchProfile` with its actual tool types and searchable predicates. URI bindings identify callable nodes; the schema profile tells SPARQL which literals to search. The default profile targets the generated schema.org/Markdown-LD vocabulary.

```csharp
options.MarkdownLdGraphSchemaSearchProfile = new KnowledgeGraphSchemaSearchProfile
{
    Prefixes = new Dictionary<string, string> { ["catalog"] = "https://catalog.example.com/vocab#" },
    TypeFilters = ["catalog:Tool"],
    TextPredicates = [new("catalog:name"), new("catalog:description")],
    RelationshipPredicates = [],
    ExpansionPredicates = [],
    TermMode = KnowledgeGraphSchemaSearchTermMode.AnyTerm
};
```

The profile type comes from `ManagedCode.MarkdownLd.Kb.Pipeline`. Per-request result limits remain gateway-owned. Schema description, validation, and search use the same configured profile.

Include that file as an `EmbeddedResource` with the matching `LogicalName`. Missing or invalid resources produce graph-index diagnostics; they are never replaced with a generated graph. Both `.json` source-document bundles and `.jsonld` graphs remain supported file inputs.

To author a JSON source-document bundle through the package:

```csharp
await using var serviceProvider = services.BuildServiceProvider();
var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
var descriptors = await gateway.ListToolsAsync();
var documents = McpGatewayMarkdownLdGraphFile.CreateDocuments(descriptors);

await McpGatewayMarkdownLdGraphFile.WriteAsync(
    "artifacts/mcp-tools.graph.json",
    documents);
```

To export the generated tool graph for RDF tooling, graph visualization, or preprocessing handoff:

```csharp
var export = await McpGatewayMarkdownLdGraphFile.ExportAsync(documents);

await File.WriteAllTextAsync("artifacts/mcp-tools.graph.jsonld", export.JsonLd);
await File.WriteAllTextAsync("artifacts/mcp-tools.graph.ttl", export.Turtle);
await File.WriteAllTextAsync("artifacts/mcp-tools.graph.mmd", export.MermaidFlowchart);
await File.WriteAllTextAsync("artifacts/mcp-tools.graph.dot", export.DotGraph);
```

To export the currently indexed runtime graph:

```csharp
var graphSearch = serviceProvider.GetRequiredService<IMcpGatewayGraphSearch>();
var export = await graphSearch.ExportMarkdownLdGraphAsync();
```

For full control over the graph input, provide the Markdown-LD documents directly:

```csharp
services.AddMcpGateway(options =>
{
    options.SearchStrategy = McpGatewaySearchStrategy.Graph;
    options.UseMarkdownLdGraphDocuments(descriptors =>
    {
        var documents = McpGatewayMarkdownLdGraphFile.CreateDocuments(descriptors).ToList();
        var githubIndex = documents.FindIndex(document =>
            document.Path.Contains("github_search_repositories", StringComparison.Ordinal));

        documents[githubIndex] = documents[githubIndex] with
        {
            Content = string.Concat(
                documents[githubIndex].Content,
                "\n\nrelease approvals merge trains")
        };

        return (IReadOnlyList<McpGatewayMarkdownLdGraphDocument>)documents;
    });
});
```

## Optional Services

### Embedding Generator

Vector search is optional. If the host registers `IEmbeddingGenerator<string, Embedding<float>>`, the gateway can use embeddings for `Embeddings` or `Auto`.

Recommended registration:

```csharp
services.AddKeyedSingleton<IEmbeddingGenerator<string, Embedding<float>>, MyEmbeddingGenerator>(
    McpGatewayServiceKeys.EmbeddingGenerator);

services.AddMcpGateway(options =>
{
    options.SearchStrategy = McpGatewaySearchStrategy.Auto;
});
```

The gateway also accepts an unkeyed embedding generator, but the keyed registration is the package-specific path.

### Query Normalization

If the host registers a keyed `IChatClient`, the gateway can normalize multilingual or noisy queries to concise English before ranking:

```csharp
services.AddKeyedSingleton<IChatClient>(
    McpGatewayServiceKeys.SearchQueryChatClient,
    mySearchRewriteChatClient);

services.AddMcpGateway(options =>
{
    options.SearchQueryNormalization =
        McpGatewaySearchQueryNormalization.TranslateToEnglishWhenAvailable;
});
```

If the keyed chat client is missing or normalization fails, search continues normally.

### Runtime Search Cache

`AddMcpGateway(...)` uses a no-op `IMcpGatewaySearchCache` by default.

To enable process-local reuse for normalized queries, query embeddings, and exact repeated search results:

```csharp
services.AddMcpGatewayInMemorySearchCache();
services.AddMcpGateway();
```

`McpGatewayInMemorySearchCache` is an adapter over a host-owned `IMemoryCache`; it does not create or dispose a private cache. The DI helper provisions `IMemoryCache`, and direct construction must pass an explicit cache from the host or test container. Entries include short TTLs and cache sizes so host-owned `MemoryCacheOptions.SizeLimit` policies can bound memory. If the host needs a different cache technology or policy, register its own `IMcpGatewaySearchCache`.

### Tool Embedding Store

For process-local embedding reuse:

```csharp
services.AddKeyedSingleton<IEmbeddingGenerator<string, Embedding<float>>, MyEmbeddingGenerator>(
    McpGatewayServiceKeys.EmbeddingGenerator);
services.AddMcpGatewayInMemoryToolEmbeddingStore();
services.AddMcpGateway(options =>
{
    options.SearchStrategy = McpGatewaySearchStrategy.Auto;
});
```

If the host needs durable or shared storage, register a custom `IMcpGatewayToolEmbeddingStore` instead:

```csharp
services.AddKeyedSingleton<IEmbeddingGenerator<string, Embedding<float>>, MyEmbeddingGenerator>(
    McpGatewayServiceKeys.EmbeddingGenerator);
services.AddSingleton<IMcpGatewayToolEmbeddingStore, MyToolEmbeddingStore>();
services.AddMcpGateway(options =>
{
    options.SearchStrategy = McpGatewaySearchStrategy.Auto;
});
```

`McpGatewayInMemoryToolEmbeddingStore` is a process-local implementation of the `IMcpGatewayToolEmbeddingStore` persistence boundary. It requires a host-owned `IMemoryCache`, either through `AddMcpGatewayInMemoryToolEmbeddingStore()` or an explicit constructor argument, so production hosts keep cache ownership, size policy, and disposal centralized.

## Routing, Meta-Tools, And Chat Integration

For category-first discovery:

```csharp
var route = await gateway.RouteToolsAsync(new McpGatewayToolRouteRequest(
    Query: "open a bridge and prepare a failover plan for incident 42",
    MaxCategories: 2,
    MaxToolsPerCategory: 2,
    ContextSummary: "incident already confirmed; the next step is an action tool",
    PreferReadOnly: false,
    IncludeDisabledTools: true));
```

`McpGatewayToolRouteResult` returns:

- `Categories` with grouped tool candidates
- `SuggestedMatches` with the flattened recommended tools
- `Diagnostics` from the underlying search path
- `RankingMode` from the underlying graph/vector/hybrid search

The router prefers safe read-only tools for discovery/inspection-style requests and uses cost and latency tiers as tie-breakers when tool quality is otherwise similar.

The gateway can expose itself as three reusable tools:

- `gateway_tools_search`
- `gateway_tools_route`
- `gateway_tool_invoke`

`McpGatewayToolSet` also exposes graph-specific tools for callers that need schema/profile inspection, explicit index rebuilds, schema/SPARQL evidence, federated SPARQL search, or graph export:

- `gateway_graph_schema_describe`
- `gateway_tool_index_build`
- `gateway_graph_schema_search`
- `gateway_graph_federated_search`
- `gateway_graph_export`

From the gateway:

```csharp
var tools = gateway.CreateMetaTools();
```

From DI:

```csharp
var toolSet = serviceProvider.GetRequiredService<McpGatewayToolSet>();
var tools = toolSet.CreateTools();
```

To add them to `ChatOptions`:

```csharp
var toolSet = serviceProvider.GetRequiredService<McpGatewayToolSet>();

var options = new ChatOptions
{
    AllowMultipleToolCalls = false
}.AddMcpGatewayTools(toolSet);
```

To add the graph/SPARQL tools to `ChatOptions`:

```csharp
var options = new ChatOptions()
    .AddMcpGatewayTools(serviceProvider)
    .AddMcpGatewayGraphTools(serviceProvider);
```

For staged auto-discovery in a chat loop, wrap any `IChatClient`:

```csharp
var innerChatClient = serviceProvider.GetRequiredService<IChatClient>();
using var chatClient = innerChatClient.UseMcpGatewayAutoDiscovery(
    serviceProvider,
    options =>
    {
        options.MaxDiscoveredTools = 2;
    });

var response = await chatClient.GetResponseAsync(
    [new ChatMessage(ChatRole.User, "Find the github search tool and run it.")],
    new ChatOptions
    {
        AllowMultipleToolCalls = false
    });
```

This flow starts with the three gateway meta-tools and only projects the latest matching tools as direct proxy tools after search.

If the host already has search results and only wants the discovered proxy tools:

```csharp
var toolSet = serviceProvider.GetRequiredService<McpGatewayToolSet>();
var discoveredTools = toolSet.CreateDiscoveredTools(search.Matches, maxTools: 3);
```

## Warmup

The gateway works with lazy indexing by default. If you want startup validation or a pre-built graph/vector index, warm it explicitly.

Manual warmup:

```csharp
await using var serviceProvider = services.BuildServiceProvider();
var build = await serviceProvider.InitializeMcpGatewayAsync();
```

Hosted warmup:

```csharp
services.AddMcpGateway(options =>
{
    options.AddTool(
        "local",
        AIFunctionFactory.Create(
            static (string query) => $"github:{query}",
            new AIFunctionFactoryOptions
            {
                Name = "github_search_repositories",
                Description = "Search GitHub repositories by user query."
            }));
});

services.AddMcpGatewayIndexWarmup();
```

## Runtime Telemetry

The gateway emits built-in .NET tracing and metrics:

- `ActivitySource`: `ManagedCode.MCPGateway`
- search activity: `ManagedCode.MCPGateway.Search`
- build activity: `ManagedCode.MCPGateway.BuildIndex`
- invocation activity: `ManagedCode.MCPGateway.Invoke`
- `Meter`: `ManagedCode.MCPGateway`

Instruments:

- `mcpgateway.search.requests`
- `mcpgateway.search.duration`
- `mcpgateway.search.vector.duration`
- `mcpgateway.search.vector.tokens`
- `mcpgateway.search.graph.duration`
- `mcpgateway.index.builds`
- `mcpgateway.index.build.duration`
- `mcpgateway.index.build.vector.tokens`
- `mcpgateway.invocation.requests`, `mcpgateway.invocation.duration`, `mcpgateway.invocation.dispatch_duration`, `mcpgateway.invocation.admission_duration`
- `mcpgateway.invocation.source_payload_bytes`, `mcpgateway.invocation.approved_payload_bytes`, `mcpgateway.invocation.delivered_bytes`
- `mcpgateway.invocation.projections`, `mcpgateway.invocation.stored_results`, `mcpgateway.result.reads`
- `mcpgateway.catalog.publications`, `mcpgateway.catalog.source_refreshes`

Search telemetry includes configured strategy, ranking mode, graph/vector usage, cache-hit state, normalization state and measured vector token usage. Result/related/next-step counts, focused graph sizes and index tool/node/edge counts are histograms rather than metric labels. Invocation labels use bounded outcomes/operations; publication, refresh and result-read labels use boolean status. Default metrics and logs contain no arguments, result content, actor IDs or credentials. Register `IMcpGatewayAuditSink` for explicit host-controlled auditing with approved pseudonymous context/references; `Execution.AuditTimeout` defaults to 30 seconds. Audit failure never retries or changes an already completed mutation. No model-token savings or monetary-cost claim is inferred from byte measurements.

`McpGatewayIndexBuildResult` also exposes:

- `ToolCount`
- `VectorizedToolCount`
- `IsVectorSearchEnabled`
- `IsGraphSearchEnabled`
- `GraphNodeCount`
- `GraphEdgeCount`
- `Diagnostics`

## Performance Benchmarks

BenchmarkDotNet benchmarks live under `benchmarks/ManagedCode.MCPGateway.Benchmarks/` and run in `Release` with allocation statistics enabled:

```bash
dotnet run -c Release --project benchmarks/ManagedCode.MCPGateway.Benchmarks/ManagedCode.MCPGateway.Benchmarks.csproj -- --filter "*"
```

Focused benchmark groups:

```bash
dotnet run -c Release --project benchmarks/ManagedCode.MCPGateway.Benchmarks/ManagedCode.MCPGateway.Benchmarks.csproj -- --filter "*Search*"
dotnet run -c Release --project benchmarks/ManagedCode.MCPGateway.Benchmarks/ManagedCode.MCPGateway.Benchmarks.csproj -- --filter "*Index*"
dotnet run -c Release --project benchmarks/ManagedCode.MCPGateway.Benchmarks/ManagedCode.MCPGateway.Benchmarks.csproj -- --filter "*ToolSet*"
```

Run focused BenchmarkDotNet commands one at a time so generated benchmark build artifacts do not contend with each other.

CI and release enforce at least 85% line coverage of the library; deterministic CI builds use explicit Coverlet source-root mapping so an empty report fails the gate. CI runs the full BenchmarkDotNet suite with `--filter "*"` after the build/test/coverage gate and uploads the complete benchmark reports as `benchmark-results`. Release builds, tests and packs independently of those benchmarks, so performance measurements do not block NuGet publication.

The current full suite includes 19 standard-job cases, including 1k/5k/10k catalogs, four concurrent authorization partitions and repeated refresh. A partial local run with published `MarkdownLd.Kb 0.2.17` completed the 1,000-tool authorized-cycle case: mean 153.703 ms, standard deviation 82.495 ms and 89.67 MiB allocated per four concurrent discovery/schema/invocation cycles. Cold setup took 44.768 s and retained 745.48 MiB of managed memory. The shared host was under memory/swap pressure; the run was stopped during 5,000-tool setup and is not full qualification or a latency guarantee. Full 19-case performance qualification is tracked through CI independently of publication.

Historical full local snapshot on October 12, 2026, Apple M2 Pro, .NET SDK `10.0.401`, runtime `.NET 10.0.12`, published `MarkdownLd.Kb 0.2.15`: the previous 12 standard-job cases completed in 17m59s with allocation statistics. Async setup, cleanup and measured paths remain async end-to-end.

| Scenario | Mean | Allocated |
| --- | ---: | ---: |
| `BuildGraphIndex` | 824.653 ms | 490.48 MiB |
| `SearchWeatherGraph` | 18.808 ms | 9.23 MiB |
| `SearchPortfolioGraph` | 8.766 ms | 9.19 MiB |
| `SearchArchiveGraph` | 16.763 ms | 20.31 MiB |
| `SearchWeatherGraphTool` | 10.878 ms | 9.23 MiB |
| `SearchArchiveGraphTool` | 15.472 ms | 20.28 MiB |
| `SearchExactIdentityGraph` | 12.306 ms | 15.03 MiB |
| `SearchNoMatchGraph` | 76.846 ms | 37.49 MiB |
| `SearchNoMatchGraphTool` | 3.353 ms | 2.98 MiB |
| `CreateGatewayTools` | 1008.4 ns | 1.60 KiB |
| `CreateGraphTools` | 1790.3 ns | 2.84 KiB |
| `CreateDiscoveredTools` | 836.9 ns | 3.10 KiB |

Timing dispersion is material in several cases; the full report includes standard deviations. Normal no-match search enables token-distance fallback, while the strict graph tool returns no candidates. See [graph-search profiling](docs/Performance/GatewaySearchProfiling.md) for measured bottlenecks and comparison boundaries.

See [docs/Performance/Benchmarks.md](docs/Performance/Benchmarks.md) for benchmark scope and optimization policy.

## Deeper Docs

- [Architecture overview](docs/Architecture/Overview.md)
- [ADR-0001: Runtime boundaries and index lifecycle](docs/ADR/ADR-0001-runtime-boundaries-and-index-lifecycle.md)
- [ADR-0002: Search ranking and query normalization](docs/ADR/ADR-0002-search-ranking-and-query-normalization.md)
- [ADR-0003: Reusable chat-client and agent auto-discovery modules](docs/ADR/ADR-0003-reusable-chat-client-and-agent-tool-modules.md)
- [ADR-0004: Process-local embedding store uses IMemoryCache](docs/ADR/ADR-0004-process-local-embedding-store-uses-imemorycache.md)
- [ADR-0005: Markdown-LD graph search for tool retrieval](docs/ADR/ADR-0005-markdown-ld-graph-search-for-tool-retrieval.md)
- [ADR-0006: Vector-first auto search and runtime telemetry](docs/ADR/ADR-0006-vector-first-auto-search-and-runtime-telemetry.md)
- [ADR-0007: Vertical-slice package organization](docs/ADR/ADR-0007-vertical-slice-package-organization.md)
- [ADR-0012: Schema-aware SPARQL graph search](docs/ADR/ADR-0012-schema-aware-sparql-graph-search.md)
- [Feature spec: Search query normalization and ranking](docs/Features/SearchQueryNormalizationAndRanking.md)
- [Feature spec: Auto vector-first search and performance](docs/Features/AutoVectorFirstSearchAndPerformance.md)
- [Performance benchmarks](docs/Performance/Benchmarks.md)

## Local Development

```bash
dotnet tool restore
dotnet restore ManagedCode.MCPGateway.slnx
dotnet build ManagedCode.MCPGateway.slnx -c Release --no-restore
dotnet test --solution ManagedCode.MCPGateway.slnx -c Release --no-build
```

Analyzer and formatting pass:

```bash
dotnet build ManagedCode.MCPGateway.slnx -c Release --no-restore -p:RunAnalyzers=true
dotnet format ManagedCode.MCPGateway.slnx --verify-no-changes
dotnet tool run roslynator analyze src/ManagedCode.MCPGateway/ManagedCode.MCPGateway.csproj tests/ManagedCode.MCPGateway.Tests/ManagedCode.MCPGateway.Tests.csproj
```

Optional opt-in `CSharpier` check:

```bash
dotnet csharpier check .
```

Coverage:

```bash
dotnet tool run coverlet tests/ManagedCode.MCPGateway.Tests/bin/Release/net10.0/ManagedCode.MCPGateway.Tests.dll --target "./tests/ManagedCode.MCPGateway.Tests/bin/Release/net10.0/ManagedCode.MCPGateway.Tests" --targetargs "" --format cobertura --output artifacts/coverage/coverage.cobertura.xml --include "[ManagedCode.MCPGateway]*" --threshold 85 --threshold-type line --threshold-stat total
dotnet tool run reportgenerator -reports:"artifacts/coverage/coverage.cobertura.xml" -targetdir:"artifacts/coverage-report" -reporttypes:"HtmlSummary;MarkdownSummaryGithub"
```

Benchmarks:

```bash
dotnet run -c Release --project benchmarks/ManagedCode.MCPGateway.Benchmarks/ManagedCode.MCPGateway.Benchmarks.csproj -- --filter "*"
```

Exact tool names and tool IDs retain their schema-backed graph match in hybrid discovery; unrelated ranked candidates cannot displace an exact identity. Natural-language queries continue to use hybrid ranking.


## CLI

Install the separate NuGet .NET tool:

```bash
dotnet tool install --global mcpgateway
mcpgateway --help
mcpgateway tools search "weather forecast" --endpoint https://gateway.example.com/mcp
mcpgateway tools schema weather_lookup --endpoint https://gateway.example.com/mcp
mcpgateway tools call weather_lookup --arguments-file request.json --output result.json
```

Set `MCPGATEWAY_ENDPOINT` to omit `--endpoint`; authenticated endpoints use the bearer token from `MCPGATEWAY_TOKEN`. The server enforces its own gateway policy. Output is JSON, diagnostics use stderr, existing output files are never overwritten, and cancellation exits with code 130. Tool errors exit with code 2; connection, input, or file errors exit with code 1. CLI publication is independent through `publish-cli.yml`.

For persisted selections across multiple HTTP servers, set `McpGatewayHttpServerOptions.UseSourceQualifiedToolIds = true`. Canonical IDs retain the source prefix before and after another server adds the same upstream tool name. The default naming behavior remains unchanged.
