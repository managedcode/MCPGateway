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
- one search surface with vector ranking when embeddings are available and lexical fallback when they are not
- one invoke surface for both local tools and MCP tools
- runtime registration through `IMcpGatewayRegistry`
- reusable gateway meta-tools for chat clients and agents
- staged tool auto-discovery for chat loops, so models do not need to see the whole catalog at once

## Core Services

After `services.AddManagedCodeMcpGateway(...)`, the container exposes:

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
var invoke = await gateway.InvokeAsync(new McpGatewayInvokeRequest(
    ToolId: search.Matches[0].ToolId,
    Query: "managedcode"));
```

Important defaults:

- search is `Auto` by default
- `Auto` uses embeddings when available and lexical fallback otherwise
- the default result size is `5`
- the maximum result size is `15`
- the index is built lazily on first list, search, or invoke

## Basic Registration

Register local tools during startup:

```csharp
services.AddManagedCodeMcpGateway(options =>
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

## Register MCP Sources

`ManagedCode.MCPGateway` supports:

- local `AITool` / `AIFunction`
- HTTP MCP servers
- stdio MCP servers
- existing `McpClient` instances
- deferred `McpClient` factories

Examples:

```csharp
services.AddManagedCodeMcpGateway(options =>
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

The gateway works without explicit initialization, but you can warm the index eagerly when you want startup validation or a pre-built cache.

Manual warmup:

```csharp
await using var serviceProvider = services.BuildServiceProvider();

var build = await serviceProvider.InitializeManagedCodeMcpGatewayAsync();
```

Hosted warmup:

```csharp
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

## Optional Embeddings

If the container has `IEmbeddingGenerator<string, Embedding<float>>`, the gateway can use vector ranking.

Preferred registration:

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

If no embedding generator is registered, the same gateway still works and falls back to lexical search automatically.

## Optional Query Normalization

If you want multilingual or noisy queries normalized before ranking, register a keyed `IChatClient`:

```csharp
var services = new ServiceCollection();

services.AddKeyedSingleton<IChatClient>(
    McpGatewayServiceKeys.SearchQueryChatClient,
    mySearchRewriteChatClient);

services.AddManagedCodeMcpGateway(options =>
{
    options.SearchStrategy = McpGatewaySearchStrategy.Auto;
    options.SearchQueryNormalization = McpGatewaySearchQueryNormalization.TranslateToEnglishWhenAvailable;
});
```

If the keyed chat client is missing or normalization fails, search continues normally.

## Optional Persistent Tool Embeddings

For process-local caching, use the built-in in-memory store:

```csharp
services.AddKeyedSingleton<IEmbeddingGenerator<string, Embedding<float>>, MyEmbeddingGenerator>(
    McpGatewayServiceKeys.EmbeddingGenerator);
services.AddSingleton<IMcpGatewayToolEmbeddingStore, McpGatewayInMemoryToolEmbeddingStore>();
```

For durable caching, register your own `IMcpGatewayToolEmbeddingStore` implementation:

```csharp
services.AddKeyedSingleton<IEmbeddingGenerator<string, Embedding<float>>, MyEmbeddingGenerator>(
    McpGatewayServiceKeys.EmbeddingGenerator);
services.AddSingleton<IMcpGatewayToolEmbeddingStore, MyToolEmbeddingStore>();
```

## Search Modes

`McpGatewaySearchStrategy.Auto` is the default and usually the right choice:

- use vector ranking when embeddings are available
- fall back to lexical ranking when they are not

You can also force a mode:

```csharp
services.AddManagedCodeMcpGateway(options =>
{
    options.SearchStrategy = McpGatewaySearchStrategy.Tokenizer;
});
```

Or:

```csharp
services.AddManagedCodeMcpGateway(options =>
{
    options.SearchStrategy = McpGatewaySearchStrategy.Embeddings;
});
```

`McpGatewaySearchResult.RankingMode` reports:

- `vector`
- `lexical`
- `browse`
- `empty`

## Deeper Docs

Use these when you need design details rather than package onboarding:

- [Architecture overview](docs/Architecture/Overview.md)
- [ADR-0001: Runtime boundaries and index lifecycle](docs/ADR/ADR-0001-runtime-boundaries-and-index-lifecycle.md)
- [ADR-0002: Search ranking and query normalization](docs/ADR/ADR-0002-search-ranking-and-query-normalization.md)
- [ADR-0003: Reusable chat-client and agent auto-discovery modules](docs/ADR/ADR-0003-reusable-chat-client-and-agent-tool-modules.md)
- [Feature spec: Search query normalization and ranking](docs/Features/SearchQueryNormalizationAndRanking.md)

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
