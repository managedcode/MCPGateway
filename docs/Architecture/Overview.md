# Architecture Overview

## Scoping (read first)

This document is the module map for `ManagedCode.MCPGateway`.

In scope:

- package boundaries
- runtime collaboration between the public facade, registry, meta-tools, warmup hooks, and internal runtime
- dependency direction between public APIs, internal modules, and optional AI services

Out of scope:

- feature-level ranking metrics
- test corpus details
- CI or release process

## Summary

`ManagedCode.MCPGateway` exposes three public DI surfaces:

- `IMcpGateway` for list/search/invoke
- `IMcpGatewayRegistry` for catalog mutation
- `McpGatewayToolSet` for reusable meta-tools

`McpGateway` stays a thin facade over `McpGatewayRuntime`, which reads immutable catalog snapshots, coordinates vector or tokenizer-backed search, optionally rewrites queries through a keyed `IChatClient`, and invokes local or MCP tools. Optional startup warmup is available through a service-provider extension or hosted background service without changing the lazy default.

## System And Module Map

```mermaid
flowchart LR
    Host["Host application"] --> DI["DI registration"]
    DI --> Facade["IMcpGateway / McpGateway"]
    DI --> Registry["IMcpGatewayRegistry / McpGatewayRegistry"]
    DI --> ToolSet["McpGatewayToolSet"]
    DI --> Warmup["Optional warmup hooks"]
    ToolSet --> Facade
    Warmup --> Facade
    Facade --> Runtime["Internal runtime orchestration"]
    Runtime --> Catalog["Internal catalog snapshots"]
    Registry --> Catalog
    Catalog --> Sources["Catalog source registrations"]
    Sources --> Local["Local AITool instances"]
    Sources --> MCP["HTTP, stdio, and provided MCP clients"]
    Runtime --> Embedder["Optional embedding generator"]
    Runtime --> Store["Optional embedding store"]
    Runtime --> Normalizer["Optional keyed search IChatClient"]
```

## Interfaces And Contracts

```mermaid
flowchart LR
    IMcpGateway["IMcpGateway"] --> McpGateway["McpGateway"]
    IMcpGatewayRegistry["IMcpGatewayRegistry"] --> Registry["McpGatewayRegistry"]
    ToolSet["McpGatewayToolSet"] --> IMcpGateway
    Warmup["McpGatewayServiceProviderExtensions / McpGatewayIndexWarmupService"] --> IMcpGateway
    McpGateway --> Runtime["McpGatewayRuntime"]
    Runtime --> SearchRequest["McpGatewaySearchRequest"]
    Runtime --> InvokeRequest["McpGatewayInvokeRequest"]
    Runtime --> Descriptor["McpGatewayToolDescriptor"]
    Runtime --> Options["McpGatewayOptions"]
    Runtime --> EmbeddingStore["IMcpGatewayToolEmbeddingStore"]
    Runtime --> ChatClient["IChatClient (keyed)"]
    Registry --> CatalogSource["IMcpGatewayCatalogSource"]
```

## Key Classes And Types

```mermaid
flowchart LR
    McpGateway["McpGateway"] --> McpGatewayRuntime["McpGatewayRuntime"]
    McpGatewayRuntime --> RuntimeCore["Internal/Runtime/Core/*"]
    McpGatewayRuntime --> RuntimeCatalog["Internal/Runtime/Catalog/*"]
    McpGatewayRuntime --> RuntimeSearch["Internal/Runtime/Search/*"]
    McpGatewayRuntime --> RuntimeInvocation["Internal/Runtime/Invocation/*"]
    McpGatewayRuntime --> RuntimeEmbeddings["Internal/Runtime/Embeddings/*"]
    Registry["McpGatewayRegistry"] --> RegistrationCollection["McpGatewayRegistrationCollection"]
    Registry --> OperationGate["McpGatewayOperationGate"]
    RegistrationCollection --> SourceRegistrations["McpGatewayToolSourceRegistration*"]
    RuntimeSearch --> Json["McpGatewayJsonSerializer"]
    Warmup["McpGatewayIndexWarmupService"] --> McpGateway
    InMemoryStore["McpGatewayInMemoryToolEmbeddingStore"] --> StoreIndex["McpGatewayToolEmbeddingStoreIndex"]
```

## Module Index

- Public facade: [`src/ManagedCode.MCPGateway/McpGateway.cs`](../../src/ManagedCode.MCPGateway/McpGateway.cs) exposes the package runtime API and delegates work to the internal runtime.
- Public abstractions: [`src/ManagedCode.MCPGateway/Abstractions/`](../../src/ManagedCode.MCPGateway/Abstractions/) defines the stable interfaces consumers resolve from DI.
- Public configuration: [`src/ManagedCode.MCPGateway/Configuration/`](../../src/ManagedCode.MCPGateway/Configuration/) contains options and service keys that shape host integration.
- Public models: [`src/ManagedCode.MCPGateway/Models/`](../../src/ManagedCode.MCPGateway/Models/) contains request/result contracts and enums grouped by search, invocation, catalog, and embeddings behavior.
- Public embeddings: [`src/ManagedCode.MCPGateway/Embeddings/`](../../src/ManagedCode.MCPGateway/Embeddings/) provides optional embedding-store implementations.
- Public meta-tools: [`src/ManagedCode.MCPGateway/McpGatewayToolSet.cs`](../../src/ManagedCode.MCPGateway/McpGatewayToolSet.cs) exposes the gateway as reusable `AITool` instances for model-driven search and invoke flows.
- Internal catalog module: [`src/ManagedCode.MCPGateway/Internal/Catalog/`](../../src/ManagedCode.MCPGateway/Internal/Catalog/) owns mutable tool-source registration state and read-only snapshots for indexing.
- Internal catalog sources: [`src/ManagedCode.MCPGateway/Internal/Catalog/Sources/`](../../src/ManagedCode.MCPGateway/Internal/Catalog/Sources/) owns transport-specific source registrations and MCP client creation.
- Internal runtime module: [`src/ManagedCode.MCPGateway/Internal/Runtime/`](../../src/ManagedCode.MCPGateway/Internal/Runtime/) owns orchestration and is split by core, catalog, search, invocation, and embeddings concerns.
- Internal embedding helpers: [`src/ManagedCode.MCPGateway/Internal/Embeddings/`](../../src/ManagedCode.MCPGateway/Internal/Embeddings/) contains non-public embedding indexing helpers.
- Internal serialization: [`src/ManagedCode.MCPGateway/Internal/Serialization/`](../../src/ManagedCode.MCPGateway/Internal/Serialization/) contains the canonical JSON materialization path used by runtime features.
- Warmup hooks: [`src/ManagedCode.MCPGateway/Registration/McpGatewayServiceProviderExtensions.cs`](../../src/ManagedCode.MCPGateway/Registration/McpGatewayServiceProviderExtensions.cs) and [`src/ManagedCode.MCPGateway/Internal/Warmup/McpGatewayIndexWarmupService.cs`](../../src/ManagedCode.MCPGateway/Internal/Warmup/McpGatewayIndexWarmupService.cs) provide optional eager index-building integration.
- DI registration: [`src/ManagedCode.MCPGateway/Registration/McpGatewayServiceCollectionExtensions.cs`](../../src/ManagedCode.MCPGateway/Registration/McpGatewayServiceCollectionExtensions.cs) wires facade, registry, meta-tools, and warmup support into the container.

## Dependency Rules

- Public code may depend on `Models`, `Configuration`, and `Abstractions`, but internal modules must not depend on tests or docs.
- `McpGateway` is a thin facade only. It may delegate to `McpGatewayRuntime`, but it must not own registry mutation logic.
- `Internal/Catalog` owns mutable source registration state. `Internal/Runtime` may read snapshots from it, but must not mutate registrations directly.
- `Internal/Catalog/Sources` owns MCP transport-specific creation and caching. Transport setup must not leak into `Internal/Runtime`, `Models`, or `Configuration`.
- `Internal/Runtime` may depend on `Internal/Catalog`, `Internal/Embeddings`, `Embeddings`, `Models`, `Configuration`, and `Abstractions`.
- Optional AI services such as embedding generators and query-normalization chat clients must stay outside the package core and be resolved through DI service keys rather than hardwired provider code.
- `Models` should stay contract-first. Internal transport, registry, or lifecycle helpers do not belong there.
- Embedding support must stay optional and isolated behind `IMcpGatewayToolEmbeddingStore` and embedding-generator abstractions.
- Warmup remains optional. The package must work correctly with lazy indexing and must not require manual initialization for every host.

## Key Decisions (ADRs)

- [`docs/ADR/ADR-0001-runtime-boundaries-and-index-lifecycle.md`](../ADR/ADR-0001-runtime-boundaries-and-index-lifecycle.md): documents the public/runtime/catalog split, DI boundaries, lazy indexing, cancellation-aware single-flight builds, and optional warmup hooks.
- [`docs/ADR/ADR-0002-search-ranking-and-query-normalization.md`](../ADR/ADR-0002-search-ranking-and-query-normalization.md): documents the default `Auto` search behavior, tokenizer-backed fallback, optional English query normalization, and mathematical ranking strategy.

## Related Docs

- [`README.md`](../../README.md)
- [`docs/ADR/ADR-0001-runtime-boundaries-and-index-lifecycle.md`](../ADR/ADR-0001-runtime-boundaries-and-index-lifecycle.md)
- [`docs/ADR/ADR-0002-search-ranking-and-query-normalization.md`](../ADR/ADR-0002-search-ranking-and-query-normalization.md)
- [`docs/Features/SearchQueryNormalizationAndRanking.md`](../Features/SearchQueryNormalizationAndRanking.md)
- [`AGENTS.md`](../../AGENTS.md)
