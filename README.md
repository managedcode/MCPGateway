# ManagedCode.MCPGateway

[![CI](https://github.com/managedcode/MCPGateway/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/managedcode/MCPGateway/actions/workflows/ci.yml)
[![Release](https://github.com/managedcode/MCPGateway/actions/workflows/release.yml/badge.svg?branch=main)](https://github.com/managedcode/MCPGateway/actions/workflows/release.yml)
[![CodeQL](https://github.com/managedcode/MCPGateway/actions/workflows/codeql.yml/badge.svg?branch=main)](https://github.com/managedcode/MCPGateway/actions/workflows/codeql.yml)
[![NuGet](https://img.shields.io/nuget/v/ManagedCode.MCPGateway.svg)](https://www.nuget.org/packages/ManagedCode.MCPGateway)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

`ManagedCode.MCPGateway` is a .NET 10 library that turns local `AITool` instances and remote MCP servers into one searchable execution surface.

The package is built on:

- `Microsoft.Extensions.AI`
- the official `ModelContextProtocol` .NET SDK
- in-memory descriptor indexing with configurable embedding or tokenizer-backed ranking

## Install

```bash
dotnet add package ManagedCode.MCPGateway
```

## What You Get

- one registry for local tools, stdio MCP servers, HTTP MCP servers, or prebuilt `McpClient` instances
- descriptor indexing that enriches search with tool name, description, required arguments, and input schema
- lazy index build on the first catalog/search/invoke operation, plus optional eager warmup hooks for startup scenarios
- configurable search strategy with embeddings or tokenizer-backed heuristic ranking
- `SearchStrategy.Auto` by default: use embeddings when available, otherwise fall back to tokenizer-backed ranking automatically
- built-in `ChatGptO200kBase` tokenizer path for tokenizer search and tokenizer fallback
- optional English query normalization before ranking when a keyed search rewrite `IChatClient` is registered
- top 5 matches by default when `maxResults` is not specified
- vector search when an `IEmbeddingGenerator<string, Embedding<float>>` is registered
- optional persisted tool embeddings through `IMcpGatewayToolEmbeddingStore`
- token-aware lexical fallback when embeddings are unavailable or vector search cannot complete
- one invoke surface for both local `AIFunction` tools and MCP tools
- optional meta-tools you can hand back to another model as normal `AITool` instances

## Quickstart

```csharp
using ManagedCode.MCPGateway;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();

services.AddManagedCodeMcpGateway(options =>
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
var selectedTool = search.Matches[0];

var invoke = await gateway.InvokeAsync(new McpGatewayInvokeRequest(
    ToolId: selectedTool.ToolId,
    Query: "managedcode"));
```

`AddManagedCodeMcpGateway(...)` does not create or configure an embedding generator for you. Vector ranking is enabled only when the same DI container also has an `IEmbeddingGenerator<string, Embedding<float>>`. The gateway first tries the keyed registration `McpGatewayServiceKeys.EmbeddingGenerator` and falls back to any regular registration. Otherwise it stays fully functional and uses lexical ranking.

The gateway builds its catalog lazily on the first `ListToolsAsync(...)`, `SearchAsync(...)`, or `InvokeAsync(...)` call. If you add more tools later through the registry, the next catalog/search/invoke operation rebuilds the index automatically. You only need an explicit warmup call when you want eager startup validation or a pre-warmed cache.

`McpGateway` is the runtime search/invoke facade. If you need to add tools or MCP sources after the container is built, resolve `IMcpGatewayRegistry` separately:

```csharp
var registry = serviceProvider.GetRequiredService<IMcpGatewayRegistry>();

registry.AddTool(
    "local",
    AIFunctionFactory.Create(
        static (string query) => $"weather:{query}",
        new AIFunctionFactoryOptions
        {
            Name = "weather_search_forecast",
            Description = "Search weather forecast and temperature information by city name."
        }));

var tools = await gateway.ListToolsAsync();
```

## Optional Eager Warmup

If you want to warm the catalog immediately after building the container, use the service-provider extension:

```csharp
await using var serviceProvider = services.BuildServiceProvider();

await serviceProvider.InitializeManagedCodeMcpGatewayAsync();
```

For hosted applications, register background warmup once and let the host trigger it on startup:

```csharp
var services = new ServiceCollection();

services.AddManagedCodeMcpGateway(options =>
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

services.AddManagedCodeMcpGatewayIndexWarmup();
```

Use eager warmup when you want fail-fast startup behavior, a warmed cache before the first request, or deterministic startup benchmarking. Otherwise the lazy default is enough.

## Context-Aware Search And Invoke

When the current turn has extra UI, workflow, or chat context, pass it through the request models:

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

The gateway uses this request context in two ways:

- search combines the query, context summary, and context values into one effective search input for embeddings or lexical fallback
- MCP invocation sends the request context in MCP `meta`
- local `AIFunction` tools can receive auto-mapped `query`, `contextSummary`, and `context` arguments when those parameters are required

## Meta-Tools

You can expose the gateway itself as two reusable `AITool` instances:

```csharp
var tools = gateway.CreateMetaTools();
```

By default this creates:

- `gateway_tools_search`
- `gateway_tool_invoke`

These tools are useful when another model should first search the gateway catalog and then invoke the selected tool.

## Search Behavior

`ManagedCode.MCPGateway` builds one descriptor document per tool from:

- tool name
- display name
- description
- required arguments
- input schema summaries

Default search profile:

- `SearchStrategy = McpGatewaySearchStrategy.Auto`
- `SearchQueryNormalization = McpGatewaySearchQueryNormalization.TranslateToEnglishWhenAvailable`
- `DefaultSearchLimit = 5`
- `MaxSearchResults = 15`

`McpGatewaySearchStrategy.Auto` means:

- vector search when an embedding generator is registered
- tokenizer-backed heuristic search when embeddings are unavailable
- tokenizer-backed fallback when vector search cannot complete for a request

The tokenizer-backed mode builds field-aware search documents from tool names, display names, descriptions, required arguments, and schema properties. Ranking then happens in two stages:

- stage 1 retrieval with BM25-style field scoring, tokenizer-term cosine similarity, and character 3-gram similarity
- stage 2 reranking over the candidate pool with calibrated coverage, lexical similarity, approximate typo matching, and tool-name evidence

This keeps the search mathematical and tokenizer-driven instead of relying on hand-written query phrase exceptions. The tokenizer-backed path uses the built-in `ChatGptO200kBase` profile for the GPT-4o / ChatGPT tokenizer family.

If an embedding generator is registered and vector search is active, the gateway vectorizes descriptor documents and uses cosine similarity plus lexical boosts. It first tries the keyed registration `McpGatewayServiceKeys.EmbeddingGenerator` and then falls back to any regular `IEmbeddingGenerator<string, Embedding<float>>`.

The embedding generator is resolved per gateway operation, so singleton, scoped, and transient DI registrations all work with index builds and search.

## Optional English Query Normalization

By default, the gateway may rewrite the incoming search query into concise English before ranking:

- it only happens when `options.SearchQueryNormalization` is enabled
- it only uses a keyed `IChatClient` registered as `McpGatewayServiceKeys.SearchQueryChatClient`
- if no keyed chat client is registered, search continues unchanged
- if normalization fails, search continues with the original query and emits a diagnostic

Preferred registration:

```csharp
var services = new ServiceCollection();

services.AddKeyedSingleton<IChatClient>(
    McpGatewayServiceKeys.SearchQueryChatClient,
    mySearchRewriteChatClient);

services.AddManagedCodeMcpGateway(options =>
{
    options.SearchStrategy = McpGatewaySearchStrategy.Auto;
    options.SearchQueryNormalization = McpGatewaySearchQueryNormalization.TranslateToEnglishWhenAvailable;

    options.AddTool(
        "local",
        AIFunctionFactory.Create(
            static (string query) => $"travel:{query}",
            new AIFunctionFactoryOptions
            {
                Name = "travel_hotel_search",
                Description = "Find hotels by city, district, amenities, breakfast, or cancellation policy."
            }));
});
```

Disable normalization when the host wants purely local tokenizer behavior:

```csharp
services.AddManagedCodeMcpGateway(options =>
{
    options.SearchStrategy = McpGatewaySearchStrategy.Tokenizer;
    options.SearchQueryNormalization = McpGatewaySearchQueryNormalization.Disabled;
});
```

The package does not register or configure an `IChatClient` for you. This keeps the gateway generic while still allowing multilingual and typo-heavy search inputs to converge to an English retrieval form when the host opts in.

`McpGatewaySearchResult.RankingMode` stays:

- `vector` for embedding-backed ranking
- `lexical` for tokenizer-backed ranking and tokenizer fallback
- `browse` when no search text/context is provided
- `empty` when the catalog is empty

In other words, the current `lexical` mode is the working tokenizer mode.

## Search Strategy Matrix

Use `McpGatewaySearchStrategy.Auto` when you want one production default that works everywhere:

- if embeddings are registered, use embeddings
- if embeddings are missing, use tokenizer ranking
- if embeddings fail for a query, fall back to tokenizer ranking

Use `McpGatewaySearchStrategy.Embeddings` when:

- embeddings are expected in that host
- you want vector search whenever it is available
- tokenizer ranking should only be the fallback path when vector search cannot complete

Use `McpGatewaySearchStrategy.Tokenizer` when:

- you want a zero-embedding deployment
- you want deterministic local search behavior without an embedding provider
- you want to compare tokenizer profiles explicitly in tests or benchmarks

## Search Strategy Configuration

Force embeddings when they are available:

```csharp
var services = new ServiceCollection();

services.AddKeyedSingleton<IEmbeddingGenerator<string, Embedding<float>>, MyEmbeddingGenerator>(
    McpGatewayServiceKeys.EmbeddingGenerator);

services.AddManagedCodeMcpGateway(options =>
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

Force tokenizer-backed ranking:

```csharp
var services = new ServiceCollection();

services.AddManagedCodeMcpGateway(options =>
{
    options.SearchStrategy = McpGatewaySearchStrategy.Tokenizer;

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

Keep the default auto strategy, but make the defaults explicit in code:

```csharp
var services = new ServiceCollection();

services.AddManagedCodeMcpGateway(options =>
{
    options.SearchStrategy = McpGatewaySearchStrategy.Auto;
    options.DefaultSearchLimit = 5;
    options.MaxSearchResults = 15;

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

If you do not register an embedding generator, the same configuration still works and automatically uses tokenizer ranking.

## Optional Embeddings

Register any provider-specific implementation of `IEmbeddingGenerator<string, Embedding<float>>` in the same DI container before building the service provider.

Preferred registration for the gateway:

```csharp
var services = new ServiceCollection();

services.AddKeyedSingleton<IEmbeddingGenerator<string, Embedding<float>>, MyEmbeddingGenerator>(
    McpGatewayServiceKeys.EmbeddingGenerator);

services.AddManagedCodeMcpGateway(options =>
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
```

Fallback when your app already exposes a regular embedding generator:

```csharp
var services = new ServiceCollection();

services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>, MyEmbeddingGenerator>();

services.AddManagedCodeMcpGateway(options =>
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
```

The keyed registration is the preferred one, so you can dedicate a specific embedder to the gateway without affecting other app services.

## Tokenizer Fallback Without Embeddings

This is the default operational fallback:

```csharp
var services = new ServiceCollection();

services.AddManagedCodeMcpGateway(options =>
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

await using var serviceProvider = services.BuildServiceProvider();
var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

var result = await gateway.SearchAsync("review qeue for managedcode prs");
```

With no embedding generator registered:

- the gateway still builds the catalog
- search uses the configured tokenizer profile and two-stage lexical ranking
- an optional keyed search rewrite `IChatClient` can normalize the query to English first
- typo-tolerant term heuristics still participate in ranking
- the result diagnostics contain `lexical_fallback`
- the default result set size is 5

If you want tokenizer search only, set `options.SearchStrategy = McpGatewaySearchStrategy.Tokenizer`.

## Persistent Tool Embeddings

For process-local caching, the package already includes `McpGatewayInMemoryToolEmbeddingStore`:

```csharp
var services = new ServiceCollection();

services.AddKeyedSingleton<IEmbeddingGenerator<string, Embedding<float>>, MyEmbeddingGenerator>(
    McpGatewayServiceKeys.EmbeddingGenerator);
services.AddSingleton<IMcpGatewayToolEmbeddingStore, McpGatewayInMemoryToolEmbeddingStore>();

services.AddManagedCodeMcpGateway(options =>
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
```

If you want to keep descriptor embeddings in a database or another persistent store, register your own `IMcpGatewayToolEmbeddingStore` implementation instead:

```csharp
var services = new ServiceCollection();

services.AddKeyedSingleton<IEmbeddingGenerator<string, Embedding<float>>, MyEmbeddingGenerator>(
    McpGatewayServiceKeys.EmbeddingGenerator);
services.AddSingleton<IMcpGatewayToolEmbeddingStore, MyToolEmbeddingStore>();

services.AddManagedCodeMcpGateway(options =>
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
```

When an index build runs, whether explicitly or through lazy/background warmup, the gateway:

- computes a descriptor-document hash per tool
- asks `IMcpGatewayToolEmbeddingStore` for matching stored vectors
- generates embeddings only for tools that are missing in the store
- upserts the newly generated vectors back into the store

This avoids recalculating tool embeddings on every rebuild while still refreshing them automatically when the descriptor document changes. Stored vectors are scoped to both the descriptor hash and the resolved embedding-generator fingerprint, so changing the provider or model automatically forces regeneration. Query embeddings are still generated at search time from the registered `IEmbeddingGenerator<string, Embedding<float>>`.

## Search Evaluation

The repository includes a tokenizer evaluation suite built around a 50-tool catalog with intentionally overlapping verbs such as `search`, `lookup`, `timeline`, and `summary`, while keeping the domain semantics separated in the descriptions.

Coverage buckets in `tests/ManagedCode.MCPGateway.Tests/Search/McpGatewayTokenizerSearchEvaluationTests.cs`:

- high relevance
- borderline / semantically adjacent tools
- multilingual
- typo / spelling mistakes
- weak-intent / underspecified commands
- irrelevant queries

The evaluation asserts:

- `top1`, `top3`, and `top5`
- mean reciprocal rank
- low-confidence behavior for irrelevant queries

The noisy-query buckets intentionally include spelling mistakes and weakly specified commands so the tokenizer path is exercised as a real fallback, not only on clean benchmark phrasing.

Current reference numbers from the repository test corpus:

- `ChatGptO200kBase`: high relevance `top1=95.65%`, `top3=100%`, `top5=100%`, `MRR=0.98`; typo `top1=100%`; weak intent `top1=100%`; irrelevant `low-confidence=100%`

## Supported Sources

- local `AITool` / `AIFunction`
- HTTP MCP servers
- stdio MCP servers
- existing `McpClient` instances
- deferred `McpClient` factories


## Local Development

```bash
dotnet restore ManagedCode.MCPGateway.slnx
dotnet build ManagedCode.MCPGateway.slnx -c Release --no-restore
dotnet test --solution ManagedCode.MCPGateway.slnx -c Release --no-build
```

Analyzer pass:

```bash
dotnet build ManagedCode.MCPGateway.slnx -c Release --no-restore -p:RunAnalyzers=true
```

Detailed TUnit output:

```bash
dotnet test --solution ManagedCode.MCPGateway.slnx -c Release --no-build --output Detailed --no-progress
```

This repository uses `TUnit` on top of `Microsoft.Testing.Platform`, so prefer the `dotnet test --solution ...` commands above. Do not assume VSTest-only flags such as `--filter` or `--logger` are available here.
