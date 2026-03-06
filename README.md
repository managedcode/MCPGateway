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
- in-memory descriptor indexing with optional embedding-based ranking

## Install

```bash
dotnet add package ManagedCode.MCPGateway
```

## What You Get

- one registry for local tools, stdio MCP servers, HTTP MCP servers, or prebuilt `McpClient` instances
- descriptor indexing that enriches search with tool name, description, required arguments, and input schema
- vector search when an `IEmbeddingGenerator<string, Embedding<float>>` is registered
- lexical fallback when embeddings are unavailable
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

await gateway.BuildIndexAsync();

var search = await gateway.SearchAsync("find github repositories", maxResults: 3);
var selectedTool = search.Matches[0];

var invoke = await gateway.InvokeAsync(new McpGatewayInvokeRequest(
    ToolId: selectedTool.ToolId,
            Query: "managedcode"));
```

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

If an embedding generator is registered, the gateway vectorizes those descriptor documents and uses cosine similarity plus a small lexical boost. If no embedding generator is present, it falls back to lexical ranking without disabling execution.

## Supported Sources

- local `AITool` / `AIFunction`
- HTTP MCP servers
- stdio MCP servers
- existing `McpClient` instances
- deferred `McpClient` factories


## Local Development

```bash
dotnet restore ManagedCode.MCPGateway.slnx
dotnet build ManagedCode.MCPGateway.slnx -c Release
dotnet test --solution ManagedCode.MCPGateway.slnx -c Release
```
