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

## Install

```bash
dotnet add package ManagedCode.MCPGateway
```

## What You Get

- one gateway for local `AITool` instances and MCP tools
- one search API with default Markdown-LD graph ranking, opt-in vector ranking, and vector-first `Auto`
- one invocation API for both local tools and MCP tools
- runtime catalog mutation through `IMcpGatewayRegistry`
- reusable gateway meta-tools for chat loops
- optional warmup, caching, query normalization, and embedding reuse

After `services.AddMcpGateway(...)`, the container exposes:

- `IMcpGateway`
- `IMcpGatewayRegistry`
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
var invoke = await gateway.InvokeAsync(new McpGatewayInvokeRequest(
    ToolId: search.Matches[0].ToolId,
    Query: "managedcode"));
```

Default behavior:

- `SearchStrategy = Graph`
- `MarkdownLdGraphSource = GeneratedToolGraph`
- `SearchQueryNormalization = TranslateToEnglishWhenAvailable`
- `DefaultSearchLimit = 5`
- `MaxSearchResults = 15`
- the index is built lazily on first list, search, or invoke

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

You can also register:

- existing `McpClient` instances through `AddMcpClient(...)`
- deferred `McpClient` factories through `AddMcpClientFactory(...)`

If you need to change the catalog after the container is built, use `IMcpGatewayRegistry`:

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

`McpGatewayInvokeResult` returns:

- `IsSuccess`
- `ToolId`
- `SourceId`
- `ToolName`
- `Output`
- `Error`

## Search Hints

If a tool should be easier to find through multilingual aliases or stable domain keywords, register explicit search hints:

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

Hints are included in descriptors, Markdown-LD graph documents, vector documents, and lexical boosts. They are the preferred way to improve multilingual discovery without hardcoded ranking exceptions.

## Search Strategies

### Graph

`Graph` is the default and does not require embeddings:

```csharp
services.AddMcpGateway(options =>
{
    options.SearchStrategy = McpGatewaySearchStrategy.Graph;
});
```

Use it when you want deterministic Markdown-LD graph retrieval with related and next-step expansion.

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

Use it when you want semantic ranking without losing graph-based related and next-step expansion.

If vector ranking is unavailable or unusable, `Auto` falls back to graph ranking and reports diagnostics.

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

## Meta-Tools And Chat Integration

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

To add them to `ChatOptions`:

```csharp
var toolSet = serviceProvider.GetRequiredService<McpGatewayToolSet>();

var options = new ChatOptions
{
    AllowMultipleToolCalls = false
}.AddMcpGatewayTools(toolSet);
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

This flow starts with the two gateway meta-tools and only projects the latest matching tools as direct proxy tools after search.

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

## Deeper Docs

- [Architecture overview](docs/Architecture/Overview.md)
- [ADR-0001: Runtime boundaries and index lifecycle](docs/ADR/ADR-0001-runtime-boundaries-and-index-lifecycle.md)
- [ADR-0002: Search ranking and query normalization](docs/ADR/ADR-0002-search-ranking-and-query-normalization.md)
- [ADR-0003: Reusable chat-client and agent auto-discovery modules](docs/ADR/ADR-0003-reusable-chat-client-and-agent-tool-modules.md)
- [ADR-0004: Process-local embedding store uses IMemoryCache](docs/ADR/ADR-0004-process-local-embedding-store-uses-imemorycache.md)
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
dotnet tool run coverlet tests/ManagedCode.MCPGateway.Tests/bin/Release/net10.0/ManagedCode.MCPGateway.Tests.dll --target "dotnet" --targetargs "test --solution ManagedCode.MCPGateway.slnx -c Release --no-build" --format cobertura --output artifacts/coverage/coverage.cobertura.xml
dotnet tool run reportgenerator -reports:"artifacts/coverage/coverage.cobertura.xml" -targetdir:"artifacts/coverage-report" -reporttypes:"HtmlSummary;MarkdownSummaryGithub"
```
