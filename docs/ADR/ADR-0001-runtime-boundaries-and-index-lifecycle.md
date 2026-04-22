# ADR-0001: Runtime Boundaries And Index Lifecycle

## Context

`ManagedCode.MCPGateway` started as a single gateway concept, but the package now has three distinct concerns:

- runtime search and invocation for local `AITool` instances and remote MCP tools
- mutable catalog registration for local tools, stdio/HTTP MCP servers, and deferred `McpClient` factories
- index lifecycle management for lazy builds, hosted warmup, and rebuilds after registry mutations

The package also needs a first-class MCP prompt surface. MCP prompts are not a single system-prompt string; they are a catalog of prompt definitions exposed by individual MCP servers. Hosts that aggregate multiple MCP servers need one place to list and retrieve those prompts without bypassing the gateway package and talking to each `McpClient` directly.

The package also needs a first-class MCP resource surface. The official `modelcontextprotocol/csharp-sdk` supports direct resources, templated resources, and reads, and this repository now treats that SDK as the MCP baseline. Hosts that aggregate multiple MCP servers need one place to list direct resources, list resource templates, and read concrete resource URIs without bypassing the gateway package and talking to each `McpClient` directly.

The package also needs a supported downstream interoperability path: if one gateway already aggregates multiple upstream MCP servers, hosts should be able to expose that aggregate back out as one MCP server without hand-writing list/call/get handlers around the package contracts.

Recent changes also made index construction cancellation-aware and single-flight, so startup warmup, shutdown, and concurrent callers do not keep issuing duplicate MCP loads or continue rebuilding after a canceled operation should stop.

Hosts also need explicit control over index timing and graph-input authoring without replacing the whole runtime. That means the lifecycle must stay visible through manual build and warmup entry points, while graph source customization stays an explicit option rather than hidden runtime behavior.

The repository needs an explicit record for these boundaries so the public package surface, internal runtime structure, and README examples stay aligned.

## Decision

`ManagedCode.MCPGateway` will keep a thin public runtime facade, a separate additive-registry surface, a separate advanced runtime-catalog control surface, separate MCP prompt-catalog and resource-catalog surfaces, and an internal runtime orchestrator with lazy, cancellation-aware single-flight index builds plus optional eager warmup integration. DI remains the primary host path, the package exposes a DI-owned factory service for isolated custom gateway instances, and hosts may re-export the aggregated catalog as one downstream MCP server through `WithMcpGatewayCatalog()`.

## Diagram

```mermaid
flowchart LR
    Host["Host application"] --> DI["AddMcpGateway(...)"]
    DI --> Gateway["IMcpGateway / McpGateway"]
    DI --> Registry["IMcpGatewayRegistry / McpGatewayRegistry"]
    DI --> CatalogRuntime["IMcpGatewayCatalogRuntime / McpGatewayRegistry"]
    DI --> PromptCatalog["IMcpGatewayPromptCatalog / McpGatewayPromptCatalog"]
    DI --> ResourceCatalog["IMcpGatewayResourceCatalog / McpGatewayResourceCatalog"]
    DI --> Factory["IMcpGatewayFactory / McpGatewayFactory"]
    DI --> ToolSet["McpGatewayToolSet"]
    DI --> McpServerExport["WithMcpGatewayCatalog()"]
    DI --> Warmup["AddMcpGatewayIndexWarmup()"]
    Factory --> Gateway
    Factory --> Registry
    Gateway --> Runtime["McpGatewayRuntime"]
    Registry --> Snapshot["Catalog snapshots"]
    PromptCatalog --> Snapshot
    ResourceCatalog --> Snapshot
    McpServerExport --> Snapshot
    Runtime --> Snapshot
    Warmup --> Runtime
    Runtime --> Search["Search / invoke / index build"]
```

## Alternatives

### Alternative 1: Keep one monolithic gateway type that also mutates the registry

Pros:

- fewer types to explain
- direct mutation calls on the same service

Cons:

- violates single responsibility for runtime versus mutation
- makes DI usage less explicit
- encourages `McpGateway` to become a god object again

### Alternative 2: Require every host to call `BuildIndexAsync()` manually

Pros:

- very explicit startup workflow
- easy to reason about in small demos

Cons:

- forces boilerplate on every consumer
- easy to forget in real hosts
- contradicts the package goal of working lazily by default

### Alternative 3: Use blocking locks around registry mutation and index lifecycle

Pros:

- straightforward first implementation
- familiar concurrency model

Cons:

- obscures cancellation and shutdown behavior
- harder to scale under concurrent search/build callers
- already caused readability and lifecycle issues in this repository

## Consequences

Positive:

- public DI wiring is explicit: `IMcpGateway` for runtime work, `IMcpGatewayRegistry` for additive registration, `IMcpGatewayCatalogRuntime` for full in-memory catalog control, `IMcpGatewayPromptCatalog` for aggregated MCP prompts, `IMcpGatewayResourceCatalog` for aggregated MCP resources, and `McpGatewayToolSet` for meta-tools
- advanced consumers get a supported custom-gateway path through the DI-owned `IMcpGatewayFactory`
- hosts can expose the aggregated catalog back out as one downstream MCP server without bypassing package contracts
- resource-capable MCP sources remain accessible through one package-owned catalog instead of forcing hosts to fan out over raw `McpClient` instances
- hosts get lazy behavior by default and optional eager warmup through `InitializeMcpGatewayAsync()` or `AddMcpGatewayIndexWarmup()`
- hosts can control graph input authoring through `McpGatewayOptions.UseMarkdownLdGraphDocuments(...)` without taking over runtime orchestration
- prompt-capable MCP sources remain accessible through one package-owned catalog instead of forcing hosts to fan out over raw `McpClient` instances
- cancellation now propagates into source loading, embedding generation, and embedding-store I/O during index builds
- runtime rebuilds after registry mutations remain automatic without forcing every host into startup code

Trade-offs:

- there are more internal collaborator types to document
- lazy behavior means startup failures may surface on first use unless the host opts into eager warmup
- single-flight lifecycle code is more subtle than a naive sequential implementation

Mitigations:

- keep `McpGateway` thin and document the boundaries in `docs/Architecture/Overview.md`
- keep README examples for both lazy default usage and eager warmup
- cover cancellation, retry-after-cancel, and concurrent build behavior with tests

## Invariants

- `IMcpGateway` MUST remain the public runtime facade for build, list, search, invoke, and meta-tool creation.
- `IMcpGatewayRegistry` MUST remain the public additive mutation surface for adding tools and MCP sources after container build.
- `IMcpGatewayCatalogRuntime` MUST own supported full-catalog in-memory clear and reconfiguration operations without reflection.
- `IMcpGatewayPromptCatalog` MUST own aggregated MCP prompt listing and source-aware prompt retrieval without forcing direct `McpClient` access on consumers.
- `IMcpGatewayResourceCatalog` MUST own aggregated MCP resource listing, resource-template listing, and source-aware reads without forcing direct `McpClient` access on consumers.
- `AddMcpGateway(...)` MUST register `IMcpGateway`, `IMcpGatewayRegistry`, `IMcpGatewayCatalogRuntime`, `IMcpGatewayPromptCatalog`, `IMcpGatewayResourceCatalog`, and `McpGatewayToolSet`.
- `IMcpGatewayFactory` MUST create the same supported runtime facade and registry contracts while resolving shared host services from DI instead of smuggling them through options bags.
- `WithMcpGatewayCatalog()` MUST export the current aggregated tool, prompt, and resource catalog through official MCP server handlers instead of a parallel app-specific adapter layer.
- Index builds MUST be lazy by default and MUST rebuild automatically after registry mutations invalidate the snapshot.
- Hosted warmup MUST stay optional and MUST use the same runtime/index path as normal gateway operations.
- Graph-input customization MUST stay explicit in options and MUST still flow through the same runtime build path as generated/file-backed graph sources.
- Cancellation of `BuildIndexAsync(...)` MUST propagate into underlying source loading and embedding work.

## Rollout And Rollback

Rollout:

1. Keep the separated facade/registry/runtime structure in `src/ManagedCode.MCPGateway/`.
2. Keep README startup guidance aligned with lazy default plus optional eager warmup.
3. Keep tests covering concurrent builds, cancellation, and post-mutation rebuild behavior.

Rollback:

1. Revert the runtime/registry split only if the package intentionally changes back to a single mutable gateway facade.
2. Remove warmup helpers only if startup prewarming is intentionally dropped as a supported scenario.

## Verification

- `dotnet restore ManagedCode.MCPGateway.slnx`
- `dotnet build ManagedCode.MCPGateway.slnx -c Release --no-restore`
- `dotnet build ManagedCode.MCPGateway.slnx -c Release --no-restore -p:RunAnalyzers=true`
- `dotnet test --solution ManagedCode.MCPGateway.slnx -c Release --no-build`
- `roslynator analyze src/ManagedCode.MCPGateway/ManagedCode.MCPGateway.csproj -p Configuration=Release --severity-level warning`
- `roslynator analyze tests/ManagedCode.MCPGateway.Tests/ManagedCode.MCPGateway.Tests.csproj -p Configuration=Release --severity-level warning`
- `cloc --include-lang=C# src tests`

## Implementation Plan (step-by-step)

1. Keep `McpGateway` as a thin public facade over `McpGatewayRuntime`.
2. Keep `McpGatewayRegistry` as the DI-managed mutation surface and snapshot source.
3. Keep `McpGatewayRuntime` responsible for lazy single-flight index builds and search/invocation orchestration.
4. Expose eager warmup through service-provider and hosted-service extensions instead of forcing manual `BuildIndexAsync()` in every host.
5. Keep cancellation and concurrency regression coverage in the search/build test suite.

## Stakeholder Notes

- Product: hosts can choose lazy startup or eager warmup without changing the public runtime API.
- Dev: runtime and mutation responsibilities are intentionally separate and must stay that way.
- QA: warmup, cancellation, and rebuild-after-mutation scenarios are first-class verification targets.
- DevOps: startup behavior is configurable; eager warmup is the fail-fast option for production hosts.
