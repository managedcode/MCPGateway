# ManagedCode.MCPGateway

[![CI](https://github.com/managedcode/MCPGateway/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/managedcode/MCPGateway/actions/workflows/ci.yml)
[![Release](https://github.com/managedcode/MCPGateway/actions/workflows/release.yml/badge.svg?branch=main)](https://github.com/managedcode/MCPGateway/actions/workflows/release.yml)
[![CodeQL](https://github.com/managedcode/MCPGateway/actions/workflows/codeql.yml/badge.svg?branch=main)](https://github.com/managedcode/MCPGateway/actions/workflows/codeql.yml)
[![NuGet](https://img.shields.io/nuget/v/ManagedCode.MCPGateway.svg)](https://www.nuget.org/packages/ManagedCode.MCPGateway)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

`ManagedCode.MCPGateway` is a .NET 10 library that turns local `AITool` instances and remote MCP servers into one searchable execution surface.

It is built on:

- `Microsoft.Extensions.AI`
- the official `ModelContextProtocol` .NET SDK

## Install

```bash
dotnet add package ManagedCode.MCPGateway
```

## What It Gives You

- one gateway for local `AITool` instances and MCP tools
- one search surface with default Markdown-LD graph ranking, opt-in vector ranking, and vector-first `Auto` supplementation
- one invoke surface for both local tools and MCP tools
- runtime registration through `IMcpGatewayRegistry`
- reusable gateway meta-tools for chat clients and agents
- staged tool auto-discovery for chat loops, so models do not need to see the whole catalog at once

## Core Services

After `services.AddMcpGateway(...)`, the container exposes:

- `IMcpGateway` for build, list, search, invoke, and meta-tool creation
- `IMcpGatewayRegistry` for adding tools or MCP sources after the container is built
- `McpGatewayToolSet` for reusable `AITool` integration helpers

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
var invoke = await gateway.InvokeAsync(new McpGatewayInvokeRequest(
    ToolId: search.Matches[0].ToolId,
    Query: "managedcode"));
```

Important defaults:

- search is `Graph` by default
- graph search uses `ManagedCode.MarkdownLd.Kb` and does not require embeddings
- embeddings are opt-in through `McpGatewaySearchStrategy.Embeddings` or `McpGatewaySearchStrategy.Auto`
- `Auto` runs vector ranking first when vectors are available, then uses the Markdown-LD graph for bounded related and next-step expansion
- `McpGatewaySearchMatch.Score` is a gateway-calibrated confidence value, not a raw backend rank
- the default result size is `5`
- the maximum result size is `15`
- the index is built lazily on first list, search, or invoke

## Basic Registration

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

If you need to add tools later, use `IMcpGatewayRegistry`:

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

Registry updates automatically invalidate the catalog. The next list, search, or invoke rebuilds the index.

## Optional Search Hints

If a tool should be discoverable through multilingual aliases, domain jargon, or stable capability keywords, register explicit search hints:

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
            ]));
});
```

These hints flow into the tool descriptor, Markdown-LD graph documents, vector documents, and lexical boosts. They are the recommended way to improve multilingual and product-specific discovery without adding hardcoded ranking exceptions.

## Register MCP Sources

`ManagedCode.MCPGateway` supports:

- local `AITool` / `AIFunction`
- HTTP MCP servers
- stdio MCP servers
- existing `McpClient` instances
- deferred `McpClient` factories

Examples:

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

Or through the runtime registry:

```csharp
var registry = serviceProvider.GetRequiredService<IMcpGatewayRegistry>();

registry.AddMcpClient(
    sourceId: "issues",
    client: existingClient,
    disposeClient: false);

registry.AddMcpClientFactory(
    sourceId: "work-items",
    clientFactory: static async cancellationToken =>
        await CreateWorkItemClientAsync(cancellationToken));
```

## Search And Invoke

The normal flow is:

1. search
2. choose a match
3. invoke by `ToolId`

```csharp
var search = await gateway.SearchAsync("find github repositories");

var invoke = await gateway.InvokeAsync(new McpGatewayInvokeRequest(
    ToolId: search.Matches[0].ToolId,
    Query: "managedcode"));
```

If the host already knows the stable tool name, invocation can target `ToolName` and `SourceId` instead:

```csharp
var invoke = await gateway.InvokeAsync(new McpGatewayInvokeRequest(
    ToolName: "github_search_repositories",
    SourceId: "local",
    Query: "managedcode"));
```

Use `SourceId` when the same tool name can exist in more than one source.

## Context-Aware Search And Invoke

You can pass UI or workflow context into search and invocation:

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

This context is used for ranking, and MCP invocations also receive it through MCP `meta`.

## Meta-Tools

The gateway can expose itself as two reusable tools:

- `gateway_tools_search`
- `gateway_tool_invoke`

From the gateway:

```csharp
var tools = gateway.CreateMetaTools();
```

From DI:

```csharp
var toolSet = serviceProvider.GetRequiredService<McpGatewayToolSet>();
var tools = toolSet.CreateTools();
```

You can also attach those tools to existing chat options or tool lists:

```csharp
var toolSet = serviceProvider.GetRequiredService<McpGatewayToolSet>();

var options = new ChatOptions
{
    AllowMultipleToolCalls = false
}.AddMcpGatewayTools(toolSet);

var tools = toolSet.AddTools(existingTools);
```

## Why Auto-Discovery

Large tool catalogs should not be pushed directly into every model turn.

The recommended flow is:

1. expose only `gateway_tools_search` and `gateway_tool_invoke`
2. let the model search the gateway catalog
3. project only the latest matching tools as direct proxy tools
4. replace that discovered set when a new search result arrives

This keeps prompts smaller and tool choice cleaner while still using the full gateway catalog behind the scenes.

## Recommended Chat Integration

For normal chat loops, use the staged wrapper:

```csharp
using ManagedCode.MCPGateway;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

await using var serviceProvider = services.BuildServiceProvider();

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

What this does:

- the first turn only exposes the two gateway meta-tools
- after a search result, the latest matches are exposed as direct proxy tools
- a later search replaces the previous discovered set

If you already have a search result and want to materialize those proxy tools yourself, use:

```csharp
var toolSet = serviceProvider.GetRequiredService<McpGatewayToolSet>();
var discoveredTools = toolSet.CreateDiscoveredTools(search.Matches, maxTools: 3);
```

## Recommended Agent Integration

The same chat wrapper works with Microsoft Agent Framework hosts:

```csharp
using ManagedCode.MCPGateway;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

await using var serviceProvider = services.BuildServiceProvider();

var innerChatClient = serviceProvider.GetRequiredService<IChatClient>();
var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
using var chatClient = innerChatClient.UseMcpGatewayAutoDiscovery(
    serviceProvider,
    options =>
    {
        options.MaxDiscoveredTools = 2;
    });

var agent = new ChatClientAgent(
    chatClient,
    instructions: "Search the gateway catalog before invoking tools.",
    name: "workspace-agent",
    tools: [],
    loggerFactory: loggerFactory,
    services: serviceProvider);

var response = await agent.RunAsync(
    "Find the github search tool and run it.",
    session: null,
    options: new ChatClientAgentRunOptions(new ChatOptions
    {
        AllowMultipleToolCalls = false
    }),
    cancellationToken: default);
```

`ManagedCode.MCPGateway` itself stays generic. The Agent Framework dependency remains in the host project.

## Optional Warmup

The gateway works without explicit initialization, but you can warm the index eagerly when you want startup validation or a pre-built cache. When Markdown-LD graph search is selected, warmup builds the graph during startup instead of waiting for the first search.

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

## Optional Embeddings

If the container has `IEmbeddingGenerator<string, Embedding<float>>`, the gateway can use vector ranking.

Preferred registration:

```csharp
var services = new ServiceCollection();

services.AddKeyedSingleton<IEmbeddingGenerator<string, Embedding<float>>, MyEmbeddingGenerator>(
    McpGatewayServiceKeys.EmbeddingGenerator);

services.AddMcpGateway(options =>
{
    options.SearchStrategy = McpGatewaySearchStrategy.Embeddings;

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
```

If vector search cannot run for a request, the gateway falls back to the same Markdown-LD graph index used by the default mode and reports a diagnostic. If you register an embedding generator but leave the default `Graph` strategy in place, the generator is not used.

## Optional Query Normalization

If you want multilingual or noisy queries normalized before ranking, register a keyed `IChatClient`:

```csharp
var services = new ServiceCollection();

services.AddKeyedSingleton<IChatClient>(
    McpGatewayServiceKeys.SearchQueryChatClient,
    mySearchRewriteChatClient);

services.AddMcpGateway(options =>
{
    options.SearchQueryNormalization = McpGatewaySearchQueryNormalization.TranslateToEnglishWhenAvailable;

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
```

If the keyed chat client is missing or normalization fails, search continues normally.

## Optional Runtime Search Cache

`AddMcpGateway(...)` uses a no-op `IMcpGatewaySearchCache` by default, so the core gateway path does not force `IMemoryCache` into every host.

If you want process-local reuse for normalized queries, query embeddings, and exact repeated search results, opt in explicitly:

```csharp
services.AddMcpGatewayInMemorySearchCache();
services.AddMcpGateway(options =>
{
    options.SearchStrategy = McpGatewaySearchStrategy.Auto;

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
```

If the host needs a different cache technology or policy, register your own `IMcpGatewaySearchCache`. The gateway runtime stays on that abstraction and does not wrap `IChatClient` or `IEmbeddingGenerator` with proxy layers.

## Optional Tool Embedding Stores

For process-local embedding reuse, use the built-in `IMemoryCache`-backed store:

```csharp
services.AddKeyedSingleton<IEmbeddingGenerator<string, Embedding<float>>, MyEmbeddingGenerator>(
    McpGatewayServiceKeys.EmbeddingGenerator);
services.AddMcpGatewayInMemoryToolEmbeddingStore();
services.AddMcpGateway(options =>
{
    options.SearchStrategy = McpGatewaySearchStrategy.Embeddings;

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
```

This built-in store reuses the application's shared `IMemoryCache` and only caches embeddings inside the current process. It is useful for local reuse, but it is not durable and does not synchronize across replicas.

.NET also provides `IDistributedCache` for out-of-process cache storage and `HybridCache` for a combined local + distributed cache model. `ManagedCode.MCPGateway` does not hardcode either dependency into the gateway runtime. If you need shared cache state across instances, implement `IMcpGatewayToolEmbeddingStore` over the cache technology your host already uses.

For multi-instance or durable caching, register your own `IMcpGatewayToolEmbeddingStore` implementation:

```csharp
services.AddKeyedSingleton<IEmbeddingGenerator<string, Embedding<float>>, MyEmbeddingGenerator>(
    McpGatewayServiceKeys.EmbeddingGenerator);
services.AddSingleton<IMcpGatewayToolEmbeddingStore, MyToolEmbeddingStore>();
services.AddMcpGateway(options =>
{
    options.SearchStrategy = McpGatewaySearchStrategy.Embeddings;

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
```

## Markdown-LD Graph Sources

By default the gateway generates Markdown-LD tool documents from the current local `AITool` and MCP catalog during index build:

```csharp
services.AddMcpGateway(options =>
{
    options.SearchStrategy = McpGatewaySearchStrategy.Graph;
    options.UseGeneratedMarkdownLdGraph();

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
```

You can also build the same Markdown-LD source documents ahead of time and point the gateway at a file or directory. This is useful when the graph should be generated in a separate step and loaded by the runtime:

```csharp
var authoringServices = new ServiceCollection();
authoringServices.AddMcpGateway(options =>
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

await using (var authoringProvider = authoringServices.BuildServiceProvider())
{
    var authoringGateway = authoringProvider.GetRequiredService<IMcpGateway>();
    var descriptors = await authoringGateway.ListToolsAsync();
    var documents = McpGatewayMarkdownLdGraphFile.CreateDocuments(descriptors);

    await McpGatewayMarkdownLdGraphFile.WriteAsync(
        "artifacts/mcp-tools.graph.json",
        documents);
}

var runtimeServices = new ServiceCollection();
runtimeServices.AddMcpGateway(options =>
{
    options.SearchStrategy = McpGatewaySearchStrategy.Graph;
    options.UseMarkdownLdGraphFile("artifacts/mcp-tools.graph.json");

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
```

`UseMarkdownLdGraphFile(...)` accepts:

- a gateway graph bundle JSON file created by `McpGatewayMarkdownLdGraphFile.WriteAsync(...)`
- a directory containing Markdown-LD source documents
- a single Markdown-LD source file supported by `ManagedCode.MarkdownLd.Kb`

The bundle is a portable set of Markdown-LD source documents, not a serialized RDF store. The runtime still builds the in-memory `ManagedCode.MarkdownLd.Kb` graph from those documents so focused graph search, related matches, and next-step matches behave the same way as generated startup mode.

If you want full control over the graph input without taking over the runtime search pipeline, provide the Markdown-LD documents directly:

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
```

This is the explicit extension point when a host wants to source graph documents from metadata, files, a database, or its own authoring pipeline while still letting `ManagedCode.MCPGateway` own the actual graph build and search flow.

## Search Modes

`McpGatewaySearchStrategy.Graph` is the default and usually the right choice for zero-cost local retrieval:

- build or load a Markdown-LD graph during index build
- use deterministic token-distance search from `ManagedCode.MarkdownLd.Kb`
- return primary matches, related matches, next-step matches, and focused graph counts
- keep invocation on the same `ToolId` flow

You can force graph mode explicitly:

```csharp
services.AddMcpGateway(options =>
{
    options.SearchStrategy = McpGatewaySearchStrategy.Graph;
});
```

Use embedding mode when the host has an embedding generator and wants vector ranking first:

```csharp
services.AddMcpGateway(options =>
{
    options.SearchStrategy = McpGatewaySearchStrategy.Embeddings;
});
```

Use `Auto` when the host wants semantic primary ranking without losing Markdown-LD graph expansion. It runs vector search first when vectors are available, keeps the vector ordering as the primary result set, and then uses the graph to add bounded related or next-step matches:

```csharp
services.AddMcpGateway(options =>
{
    options.SearchStrategy = McpGatewaySearchStrategy.Auto;
});
```

Graph mode uses `ManagedCode.MarkdownLd.Kb` to convert every local `AITool` and MCP tool descriptor into an in-memory Markdown-LD knowledge graph. Each tool becomes a Markdown document with structured front matter, source metadata, required arguments, input schema text, graph groups, related-tool hints, and next-step hints. Search uses the graph's deterministic Tiktoken token-distance focused search to rank tool documents and returns normal `McpGatewaySearchMatch` results, so invocation still uses the same `ToolId` flow.

The gateway calibrates graph-facing `Score` values before returning them to callers. This keeps obviously weak multilingual or noisy matches from surfacing with fake perfect confidence and emits a `low_confidence_results` diagnostic when the top graph hit is still weak after calibration.

In `Auto` mode, multilingual or noisy queries can preserve both the original query text and the optional English-normalized rewrite for vector ranking. The Markdown-LD graph then runs as a bounded supplement over the semantic candidate window. `RankingMode = "hybrid"` is returned when graph expansion contributes related or next-step matches. If vector search is unavailable or unusable, `Auto` falls back to graph ranking and reports diagnostics.

## Runtime Telemetry

The gateway emits built-in .NET diagnostics so hosts can observe index and search behavior without extra packages:

- `ActivitySource`: `ManagedCode.MCPGateway`
- search activity: `ManagedCode.MCPGateway.Search`
- build activity: `ManagedCode.MCPGateway.BuildIndex`
- `Meter`: `ManagedCode.MCPGateway`
- instruments:
  - `mcpgateway.search.requests`
  - `mcpgateway.search.duration`
  - `mcpgateway.search.vector.duration`
  - `mcpgateway.search.graph.duration`
  - `mcpgateway.index.builds`
  - `mcpgateway.index.build.duration`

Search telemetry includes configured strategy, ranking mode, whether vector and graph ranking were used, cache-hit state, normalization state, result counts, and focused graph counts. Build telemetry includes tool counts, vectorized counts, and graph readiness/counts.

The old separate local tokenizer strategy is intentionally not exposed. Token-based search is provided by `ManagedCode.MarkdownLd.Kb` inside the Markdown-LD graph path.

`McpGatewaySearchResult.RankingMode` reports:

- `hybrid`
- `vector`
- `graph`
- `browse`
- `empty`

`McpGatewayIndexBuildResult` also reports graph index state through `IsGraphSearchEnabled`, `GraphNodeCount`, and `GraphEdgeCount`. These values are useful for startup validation and tests when a host requires graph-backed search to be available.

## Deeper Docs

Use these when you need design details rather than package onboarding:

- [Architecture overview](docs/Architecture/Overview.md)
- [ADR-0001: Runtime boundaries and index lifecycle](docs/ADR/ADR-0001-runtime-boundaries-and-index-lifecycle.md)
- [ADR-0002: Search ranking and query normalization](docs/ADR/ADR-0002-search-ranking-and-query-normalization.md)
- [ADR-0003: Reusable chat-client and agent auto-discovery modules](docs/ADR/ADR-0003-reusable-chat-client-and-agent-tool-modules.md)
- [ADR-0005: Markdown-LD graph search for tool retrieval](docs/ADR/ADR-0005-markdown-ld-graph-search-for-tool-retrieval.md)
- [ADR-0006: Vector-first auto search and runtime telemetry](docs/ADR/ADR-0006-vector-first-auto-search-and-runtime-telemetry.md)
- [Feature spec: Search query normalization and ranking](docs/Features/SearchQueryNormalizationAndRanking.md)
- [Feature spec: Auto vector-first search and performance](docs/Features/AutoVectorFirstSearchAndPerformance.md)

## Local Development

```bash
dotnet tool restore
dotnet restore ManagedCode.MCPGateway.slnx
dotnet build ManagedCode.MCPGateway.slnx -c Release --no-restore
dotnet test --solution ManagedCode.MCPGateway.slnx -c Release --no-build
```

Analyzer pass:

```bash
dotnet build ManagedCode.MCPGateway.slnx -c Release --no-restore -p:RunAnalyzers=true
dotnet tool run roslynator analyze src/ManagedCode.MCPGateway/ManagedCode.MCPGateway.csproj tests/ManagedCode.MCPGateway.Tests/ManagedCode.MCPGateway.Tests.csproj
dotnet format ManagedCode.MCPGateway.slnx --verify-no-changes
```

Coverage and human-readable report:

```bash
dotnet tool run coverlet tests/ManagedCode.MCPGateway.Tests/bin/Release/net10.0/ManagedCode.MCPGateway.Tests.dll --target "dotnet" --targetargs "test --solution ManagedCode.MCPGateway.slnx -c Release --no-build" --format cobertura --output artifacts/coverage/coverage.cobertura.xml
dotnet tool run reportgenerator -reports:"artifacts/coverage/coverage.cobertura.xml" -targetdir:"artifacts/coverage-report" -reporttypes:"HtmlSummary;MarkdownSummaryGithub"
```

The local tool manifest currently owns `roslynator`, `coverlet.console`, `reportgenerator`, `dotnet-stryker`, and `csharpier`.

- `dotnet format` remains the repository's formatter of record.
- `csharpier` is installed for opt-in checks only and is not part of the default CI path.
- `dotnet-stryker` is installed for focused mutation runs only and is not part of the default fast-path CI checks.
