# ADR-0004: Process-Local Embedding Store Uses IMemoryCache

## Context

`ManagedCode.MCPGateway` exposes `IMcpGatewayToolEmbeddingStore` so hosts can reuse tool embeddings between index builds.

The package still needs a built-in process-local option for hosts that only want cheap embedding reuse inside one app instance, but horizontal scale, durability, and cross-replica cache coherence remain separate concerns.

## Decision

The built-in `McpGatewayInMemoryToolEmbeddingStore` will use `IMemoryCache` for process-local embedding reuse, and the package will expose a dedicated `AddMcpGatewayInMemoryToolEmbeddingStore()` registration path that wires the store to the host's shared cache services.

Durable or distributed embedding reuse will remain the responsibility of host-provided `IMcpGatewayToolEmbeddingStore` implementations.

## Diagram

```mermaid
flowchart LR
    Host["Host application"] --> DI["AddManagedCodeMcpGateway(...)"]
    Host --> CacheRegistration["AddMcpGatewayInMemoryToolEmbeddingStore()"]
    CacheRegistration --> MemoryCache["IMemoryCache"]
    CacheRegistration --> Store["IMcpGatewayToolEmbeddingStore"]
    Store --> Runtime["McpGatewayRuntime"]
    Runtime --> Search["Index build / vector reuse"]
    Host --> CustomStore["Custom durable store (optional)"]
    CustomStore --> Runtime
```

## Alternatives

### Alternative 1: Ship no built-in process-local embedding store

Pros:

- smallest core package surface
- no opinionated local cache behavior

Cons:

- worse onboarding for hosts that only need local embedding reuse
- forces boilerplate for a very common optional scenario

### Alternative 2: Make the built-in store durable or distributed

Pros:

- stronger scaling story out of the box
- state can survive process restarts

Cons:

- introduces infrastructure and configuration assumptions into the core package
- forces dependency choices that belong to the host
- conflicts with the package goal of keeping embedding persistence optional

### Alternative 3: Depend directly on distributed cache abstractions in runtime code

Pros:

- shared cache behavior is explicit in the package internals
- less host code for cache-backed multi-instance deployments

Cons:

- couples gateway runtime code to a specific cache family
- weakens the current abstraction boundary around `IMcpGatewayToolEmbeddingStore`
- makes single-instance local reuse heavier than necessary

## Consequences

Positive:

- the built-in store relies on a standard .NET caching primitive
- hosts can register the process-local store with one DI call and reuse the shared `IMemoryCache`
- process-local cache behavior stays explicitly separate from durable/distributed storage concerns
- fingerprint-agnostic lookups become deterministic by reusing the latest cached embedding for the same tool document

Trade-offs:

- the package takes a new runtime dependency on `Microsoft.Extensions.Caching.Memory`
- the built-in store is still process-local only and does not solve multi-instance cache sharing
- direct construction without DI now owns a private `MemoryCache` instance and must be disposed like any other cache owner

Mitigations:

- keep `IMcpGatewayToolEmbeddingStore` as the only abstraction consumed by runtime code
- document clearly that `AddMcpGatewayInMemoryToolEmbeddingStore()` is for process-local reuse only
- keep durable/distributed examples based on host-provided store implementations

## Invariants

- `McpGatewayRuntime` MUST continue to depend only on `IMcpGatewayToolEmbeddingStore`, not on `IMemoryCache` directly.
- `McpGatewayInMemoryToolEmbeddingStore` MUST remain optional and MUST NOT become a mandatory dependency for gateway usage.
- `AddMcpGatewayInMemoryToolEmbeddingStore()` MUST register the built-in store through the host `IServiceCollection` and MUST provision `IMemoryCache`.
- Hosts that need cross-instance persistence or replication MUST continue to provide their own `IMcpGatewayToolEmbeddingStore`.
- The built-in store MUST clone vectors on read/write boundaries so callers cannot mutate cached embedding buffers in place.

## Rollout And Rollback

Rollout:

1. Add the `Microsoft.Extensions.Caching.Memory` dependency to the package.
2. Implement `McpGatewayInMemoryToolEmbeddingStore` with `IMemoryCache`.
3. Expose `AddMcpGatewayInMemoryToolEmbeddingStore()` for host DI registration.
4. Update README and architecture docs to distinguish process-local cache reuse from durable storage.

Rollback:

1. Remove `AddMcpGatewayInMemoryToolEmbeddingStore()` only if the package intentionally stops shipping a built-in process-local embedding store.
2. Keep `IMcpGatewayToolEmbeddingStore` as the only runtime dependency boundary unless the package intentionally adopts a different cache abstraction.

## Verification

- `dotnet restore ManagedCode.MCPGateway.slnx`
- `dotnet build ManagedCode.MCPGateway.slnx -c Release --no-restore`
- `dotnet build ManagedCode.MCPGateway.slnx -c Release --no-restore -p:RunAnalyzers=true`
- `dotnet test --solution ManagedCode.MCPGateway.slnx -c Release --no-build`
- `roslynator analyze src/ManagedCode.MCPGateway/ManagedCode.MCPGateway.csproj -p Configuration=Release --severity-level warning`
- `roslynator analyze tests/ManagedCode.MCPGateway.Tests/ManagedCode.MCPGateway.Tests.csproj -p Configuration=Release --severity-level warning`
- `cloc --include-lang=C# src tests`

## Implementation Plan (step-by-step)

1. Register `Microsoft.Extensions.Caching.Memory` as a package dependency.
2. Implement the built-in store with `IMemoryCache` lookups keyed by tool id, document hash, and embedding fingerprint.
3. Keep the runtime abstraction unchanged by continuing to consume `IMcpGatewayToolEmbeddingStore`.
4. Add tests for the cache-backed store, including deterministic fingerprint-agnostic fallback behavior.
5. Update README and architecture docs so process-local cache reuse is not described as durable persistence.

## Stakeholder Notes

- Product: the package still offers a zero-infrastructure local cache option, but durable storage remains opt-in.
- Dev: use `AddMcpGatewayInMemoryToolEmbeddingStore()` when the host only needs process-local embedding reuse.
- QA: verify vector reuse, clone safety, and fallback-to-latest behavior through the embedding-store tests.
- DevOps: multi-instance deployments still need a host-provided durable/distributed `IMcpGatewayToolEmbeddingStore`.
