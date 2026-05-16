# ManagedCode.MCPGateway

[![CI](https://github.com/managedcode/MCPGateway/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/managedcode/MCPGateway/actions/workflows/ci.yml)
[![Release](https://github.com/managedcode/MCPGateway/actions/workflows/release.yml/badge.svg?branch=main)](https://github.com/managedcode/MCPGateway/actions/workflows/release.yml)
[![CodeQL](https://github.com/managedcode/MCPGateway/actions/workflows/codeql.yml/badge.svg?branch=main)](https://github.com/managedcode/MCPGateway/actions/workflows/codeql.yml)
[![NuGet](https://img.shields.io/nuget/v/ManagedCode.MCPGateway.svg)](https://www.nuget.org/packages/ManagedCode.MCPGateway)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

`ManagedCode.MCPGateway` is a .NET 10 library that turns local `AITool` instances and remote MCP servers into one searchable and invokable execution surface for `Microsoft.Extensions.AI`.

It is built on:

- `Microsoft.Extensions.AI`
- the official `ModelContextProtocol` .NET SDK

`ManagedCode.MCPGateway` treats the official [`modelcontextprotocol/csharp-sdk`](https://github.com/modelcontextprotocol/csharp-sdk) as its MCP protocol baseline. The package builds on top of that SDK rather than replacing it with a narrower custom protocol layer, and the shipped gateway surface now includes aggregated MCP `tools`, gateway-owned and upstream MCP `prompts`, and MCP `resources` plus downstream MCP export support for `completion`, `prompt list-change notifications`, `resource subscriptions`, `logging/setLevel`, and task-backed MCP tool execution.

## Install

```bash
dotnet add package ManagedCode.MCPGateway
```

## What You Get

- one gateway for local `AITool` instances and MCP tools
- one prompt catalog for source-aware MCP prompts plus gateway-owned custom and composite prompts
- one resource catalog for MCP resources and resource templates aggregated across registered MCP sources
- one downstream MCP server export path over the aggregated tool, prompt, and resource catalogs with stable MCP protocol parity for completions, prompt list-change notifications, resource subscriptions, logging level changes, and task-backed tool execution
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
- `MaxSearchResults = 15`
- the index is built lazily on first list, search, or invoke

`Hybrid` means the Markdown-LD graph path runs schema-aware SPARQL search first, then uses the gateway-built ranked graph candidate path as supporting evidence and fuzzy fallback for noisy queries. It is not a tokenizer-only search path.

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

`AddHttpServer(...)` uses the official MCP C# SDK Streamable HTTP transport for modern remote MCP endpoints and keeps the source registered as an HTTP MCP source in gateway descriptors and downstream export metadata.
Use the overload with `HttpTransportMode` only when a legacy endpoint requires `AutoDetect` or `Sse`.
Use `McpGatewayHttpServerOptions` when a host needs the SDK HTTP transport knobs such as additional headers, connection timeout, known session id, session ownership, OAuth options, or SSE reconnection settings.

You can also register:

- existing `McpClient` instances through `AddMcpClient(...)`
- deferred `McpClient` factories through `AddMcpClientFactory(...)`

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
- forwarded `notifications/prompts/list_changed` when upstream or gateway-owned prompts change
- resource subscriptions through MCP `resources/subscribe` and `resources/unsubscribe`, including forwarded `notifications/resources/updated`
- logging level changes through MCP `logging/setLevel`
- task-backed tool execution through MCP `tools/call` with `task` metadata plus MCP `tasks/list`, `tasks/get`, `tasks/result`, and `tasks/cancel`
- forwarded `notifications/tasks/status` for exported gateway tasks

Exported MCP tool and prompt names are source-qualified gateway ids such as `docs:search_repository`, `ops:deployment_review_system_prompt`, or `local:release_review_bundle`, so multiple upstream servers and gateway-owned prompts can be combined without name collisions. Exported MCP resource URIs and URI templates are rewritten into gateway-owned opaque URIs so downstream `resources/read` calls route back to the correct upstream source even when multiple servers expose overlapping URI spaces. The same source-aware rewrite is also used for `completion/complete`, forwarded prompt list changes, and forwarded resource update notifications, so downstream clients always talk in terms of gateway-owned prompt names and resource URIs while the gateway proxies the corresponding upstream MCP operations. When an upstream MCP tool already advertises task support, the gateway preserves that contract on the exported tool and proxies the corresponding upstream task flow. Local gateway tools are exported as optional task-capable tools and are executed through the gateway-owned task store.

If the downstream MCP host cannot use the default singleton `IMcpGateway`, `IMcpGatewayPromptCatalog`, and `IMcpGatewayResourceCatalog` registrations directly, register a custom `IMcpGatewayServerBindingResolver`. The resolver can create or select a request-specific or session-specific gateway instance and return it through `McpGatewayServerBinding`, while `WithMcpGatewayCatalog()` continues to own the exported MCP handlers, prompt/resource notifications, subscriptions, and task flow:

```csharp
services.AddMcpGateway();

services.AddSingleton<IMcpGatewayServerBindingResolver>(serviceProvider =>
    new RouteScopedGatewayBindingResolver(
        serviceProvider.GetRequiredService<IMcpGatewayFactory>()));

services.AddMcpServer()
    .WithHttpTransport()
    .WithMcpGatewayCatalog();
```

Use the default resolver when one singleton aggregated gateway is enough. Use a custom binding resolver when the exported MCP endpoint needs to select a different gateway instance per downstream route, tenant, or authenticated session.

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

Use it when you want deterministic Markdown-LD graph retrieval with related and next-step expansion. The default graph mode is schema-aware `Hybrid`: it asks `ManagedCode.MarkdownLd.Kb` to generate and execute schema-scoped SPARQL against the tool graph, then merges gateway-ranked graph candidate results as supporting evidence. If schema search finds no mapped gateway tools, hybrid mode enables fuzzy token matching in that candidate fallback so typo-heavy queries such as `trak shipmnt` can still map to shipment-tracking tools without embeddings. Large catalogs use a bounded candidate-backed schema path instead of an unbounded full-graph SPARQL pass.

```csharp
services.AddMcpGateway(options =>
{
    options.SearchStrategy = McpGatewaySearchStrategy.Graph;
    options.UseHybridMarkdownLdGraphSearch();
});
```

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
- schema prefixes, text predicates, relationship predicates, expansion predicates, and type filters
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

## Graph Sources

By default the gateway generates Markdown-LD tool documents from the current catalog during index build:

```csharp
services.AddMcpGateway(options =>
{
    options.SearchStrategy = McpGatewaySearchStrategy.Graph;
    options.UseGeneratedMarkdownLdGraph();
});
```

You can also point the runtime at a previously written graph bundle, a Markdown-LD file, or a directory:

```csharp
services.AddMcpGateway(options =>
{
    options.SearchStrategy = McpGatewaySearchStrategy.Graph;
    options.UseMarkdownLdGraphFile("artifacts/mcp-tools.graph.json");
});
```

To author a bundle through the package:

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

If the host needs a different cache technology or policy, register its own `IMcpGatewaySearchCache`.

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

Search telemetry includes configured strategy, ranking mode, graph/vector usage, cache-hit state, normalization state, result counts, focused graph counts, and vector token usage. Build telemetry includes tool counts, graph state, vectorized tool counts, duration, and vector token usage.

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

CI runs the full BenchmarkDotNet suite with `--filter "*"` after the build/test gate and uploads the complete benchmark reports as `benchmark-results`. The release workflow also runs the full suite before package creation and uploads `release-benchmark-results`. These benchmark jobs are intentionally not smoke tests or reduced benchmark subsets.

Latest full local BenchmarkDotNet snapshot on May 4, 2026, Apple M2 Pro, .NET SDK `10.0.201`, runtime `.NET 10.0.5`. Benchmark setup, cleanup, and measured async paths run async end-to-end without sync-over-async blocking:

| Scenario | Mean | Allocated |
| --- | ---: | ---: |
| `BuildGraphIndex` | 608.6 ms | 500.46 MB |
| `SearchWeatherGraph` | 25.22 ms | 25.88 MB |
| `SearchPortfolioGraph` | 24.55 ms | 26.07 MB |
| `SearchArchiveGraph` | 122.17 ms | 108.64 MB |
| `SearchWeatherGraphTool` | 24.40 ms | 25.92 MB |
| `SearchArchiveGraphTool` | 111.05 ms | 108.10 MB |
| `CreateGatewayTools` | 568.5 ns | 936 B |
| `CreateGraphTools` | 1.094 us | 1,528 B |
| `CreateDiscoveredTools` | 786.0 ns | 3,192 B |

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
dotnet tool run coverlet tests/ManagedCode.MCPGateway.Tests/bin/Release/net10.0/ManagedCode.MCPGateway.Tests.dll --target "./tests/ManagedCode.MCPGateway.Tests/bin/Release/net10.0/ManagedCode.MCPGateway.Tests" --targetargs "" --format cobertura --output artifacts/coverage/coverage.cobertura.xml
dotnet tool run reportgenerator -reports:"artifacts/coverage/coverage.cobertura.xml" -targetdir:"artifacts/coverage-report" -reporttypes:"HtmlSummary;MarkdownSummaryGithub"
```

Benchmarks:

```bash
dotnet run -c Release --project benchmarks/ManagedCode.MCPGateway.Benchmarks/ManagedCode.MCPGateway.Benchmarks.csproj -- --filter "*"
```
