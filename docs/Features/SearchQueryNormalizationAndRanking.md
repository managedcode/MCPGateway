# Search Query Normalization And Ranking

## Purpose And Scope

This feature improves `ManagedCode.MCPGateway` search quality for multilingual, typo-heavy, and weakly specified search requests without introducing phrase-level hardcoded rules or a mandatory embedding dependency.

In scope:

- optional English query normalization before ranking
- Markdown-LD focused graph ranking as the default search path
- opt-in vector ranking with graph fallback
- vector-first `Auto` ranking with bounded Markdown-LD graph supplementation
- explicit tool search hints for aliases and keywords
- gateway-level confidence calibration for graph-ranked results
- file-system Markdown-LD graph sources for pre-generated graph documents
- host-supplied Markdown-LD graph documents for explicit index-input control
- deterministic behavior when no AI normalizer or embedding generator is registered
- automated verification for graph, vector fallback, multilingual, noisy, and focused expansion scenarios

Out of scope:

- embedding model changes
- vendor-specific AI SDK setup inside the package
- domain-specific synonym lists or handcrafted query exceptions
- exposing a separate local tokenizer strategy

## Affected Modules

- `src/ManagedCode.MCPGateway/Configuration/McpGatewayOptions.cs`
- `src/ManagedCode.MCPGateway/Configuration/McpGatewayMarkdownLdGraphSource.cs`
- `src/ManagedCode.MCPGateway/Configuration/McpGatewayServiceKeys.cs`
- `src/ManagedCode.MCPGateway/Models/Search/*`
- `src/ManagedCode.MCPGateway/Internal/Runtime/Graph/*`
- `src/ManagedCode.MCPGateway/Internal/Runtime/Search/*`
- `tests/ManagedCode.MCPGateway.Tests/Search/*`
- `README.md`

## Business Rules

1. Graph-backed search must be the default and must stay functional with zero embedding or chat-model dependencies.
2. Embedding search must be opt-in and must fall back to Markdown-LD graph ranking when vector search cannot complete.
3. `Auto` may exist as an explicit policy mode, but it must not be documented as the default or as a third retrieval engine.
4. `Auto` must run vector ranking first when vectors are available and usable, then use Markdown-LD graph search only for bounded related or next-step supplementation.
5. Token-based retrieval must come from `ManagedCode.MarkdownLd.Kb` inside the graph path; the package must not expose a separate local `Tokenizer` strategy.
6. Markdown-LD graph mode must support generated tool documents at index build/startup and file-system graph sources through a configured path.
7. Markdown-LD graph mode should also allow host-supplied Markdown-LD documents so developers can control graph authoring without replacing the gateway runtime.
8. File-backed graph tests must generate graph fixtures through package APIs or generated Markdown-LD documents, not hand-authored static artifacts.
9. When query normalization is enabled and a keyed `IChatClient` is available, the gateway must normalize the user query into concise English before ranking.
10. Query normalization must preserve identifiers and retrieval-critical literals such as emails, repository names, CVE references, order numbers, tracking numbers, and SKUs.
11. If normalization is enabled but no keyed normalizer client is registered, the gateway must continue with the original query and must not fail the search.
12. If normalization fails, the gateway must continue with the original query and expose a diagnostic rather than throwing.
13. Search-quality improvements must prefer mathematical or graph-ranking changes over text-level hardcoded exceptions.
14. Gateway-facing `McpGatewaySearchMatch.Score` values must be calibrated confidence values rather than raw graph-library ranks, because callers need trustworthy confidence for multilingual and noisy queries.
15. Low-confidence graph results must emit a diagnostic instead of surfacing fake perfect confidence.
16. Developers must be able to attach explicit aliases and keywords to tools so multilingual and product-specific discovery can improve without adding hardcoded ranking exceptions.
17. Default search result limits and existing public search/invoke entry points must remain intact.

## Main Flow

```mermaid
flowchart LR
    Request["Search request"] --> Normalize{"English normalization enabled\nand keyed IChatClient present?"}
    Normalize -->|Yes| Rewrite["Rewrite query to concise English"]
    Normalize -->|No| Original["Use original query"]
    Rewrite --> Strategy{"Selected strategy"}
    Original --> Strategy
    Strategy -->|Graph / MarkdownLd| GraphSource{"Markdown-LD source"}
    GraphSource -->|GeneratedToolGraph| Generated["Generate tool documents\nfrom catalog snapshot"]
    GraphSource -->|FileSystem| FileDocs["Load bundle file,\ndirectory, or source file"]
    GraphSource -->|CustomDocuments| CustomDocs["Use host-supplied\nMarkdown-LD documents"]
    Generated --> Graph["Focused graph search"]
    FileDocs --> Graph
    CustomDocs --> Graph
    Strategy -->|Embeddings| Vector["Embedding ranking"]
    Strategy -->|Auto| Policy["Vector-first hybrid policy"]
    Vector -->|Failure / unusable vector| Graph
    Policy -->|Vector unavailable / unusable| Graph
    Policy -->|Vector available| AutoGraph["Bounded graph supplementation"]
    Graph --> Result["Primary + related + next-step matches"]
    AutoGraph --> Result
    Vector --> Result
```

## Negative And Edge Cases

- Empty query with no context still returns `browse` mode.
- Empty catalog still returns `empty` mode.
- Default graph mode must not call a registered embedding generator.
- Forced graph mode must build or load the graph during explicit init, lazy first use, or hosted warmup.
- Missing file-system graph path must be reported as a diagnostic and must not crash list/search/invoke.
- A registered embedding generator is used only when the selected strategy allows vector search.
- `Auto` must not let graph-only noise override a strong semantic primary result.
- A normalization client that returns blank output must not replace the original query.
- A normalization client that times out or throws must emit a diagnostic and fall back to the original query.
- Typo-heavy inputs such as `shipmnt` must still retrieve the expected tool through Markdown-LD token-distance graph search.
- Clearly irrelevant multilingual or noisy graph matches must not surface with `Score = 1`.

## System Behavior

- Entry points:
  - `IMcpGateway.SearchAsync(string?, int?, CancellationToken)`
  - `IMcpGateway.SearchAsync(McpGatewaySearchRequest, CancellationToken)`
- Reads:
  - tool catalog snapshot from `IMcpGatewayCatalogSource`
  - optional tool search hints from local registration metadata or tool annotations/additional properties
  - keyed optional search normalizer client from DI
  - optional embedding generator and embedding store from DI when vector strategy is selected
  - search and graph-source options from `McpGatewayOptions`
  - file-system Markdown-LD graph source when `MarkdownLdGraphSource.FileSystem` is selected
  - host-supplied Markdown-LD graph documents when `UseMarkdownLdGraphDocuments(...)` is selected
- Writes:
  - no persistent writes beyond existing optional embedding-store behavior
  - optional process-local cache entries through `IMcpGatewaySearchCache` for normalized queries, query embeddings, and repeated search results
  - graph bundle authoring uses `McpGatewayMarkdownLdGraphFile.WriteAsync(...)` when the host chooses to generate a file
- Side effects:
  - optional `IChatClient` request for query normalization per unique query until the process-local cache entry expires
  - in-memory Markdown-LD graph construction during index build for graph-capable strategies
  - optional query embedding generation per unique vector query until the process-local cache entry expires
  - diagnostics describing normalization fallback, vector fallback, graph-source problems, or low-confidence graph conditions
- Idempotency:
  - same indexed catalog, same graph source, and same deterministic query-normalizer response yield stable ranking
- Errors:
  - search must not throw only because the optional normalizer is missing or fails
  - graph build failures must be diagnostic-only

## Verification

Environment assumptions:

- .NET 10 SDK from `global.json`
- `TUnit` on `Microsoft.Testing.Platform`

Verification commands:

- `dotnet restore ManagedCode.MCPGateway.slnx`
- `dotnet build ManagedCode.MCPGateway.slnx -c Release --no-restore`
- `dotnet build ManagedCode.MCPGateway.slnx -c Release --no-restore -p:RunAnalyzers=true`
- `dotnet test --solution ManagedCode.MCPGateway.slnx -c Release --no-build`

Test mapping:

- normalization success and fallback behavior in `tests/ManagedCode.MCPGateway.Tests/Search/McpGatewaySearchMarkdownLdTests.cs`
- explicit search-hint coverage in `tests/ManagedCode.MCPGateway.Tests/Search/McpGatewaySearchMarkdownLdTests.cs`
- generated graph ranking coverage in `tests/ManagedCode.MCPGateway.Tests/Search/McpGatewaySearchGraphTests.cs`
- file-backed graph bundle and directory coverage in `tests/ManagedCode.MCPGateway.Tests/Search/McpGatewaySearchGraphTests.cs`
- embedding fallback coverage in `tests/ManagedCode.MCPGateway.Tests/Search/McpGatewaySearchMarkdownLdTests.cs`
- default graph/no-embedding coverage in `tests/ManagedCode.MCPGateway.Tests/Search/McpGatewaySearchBuildTests.cs`
- confidence calibration coverage in `tests/ManagedCode.MCPGateway.Tests/Search/McpGatewaySearchConfidenceTests.cs`
- `Auto` vector-first and graph-supplement coverage in `tests/ManagedCode.MCPGateway.Tests/Search/McpGatewaySearchAutoTests.cs`
- telemetry coverage in `tests/ManagedCode.MCPGateway.Tests/Search/McpGatewayTelemetryTests.cs`
- performance-smoke coverage in `tests/ManagedCode.MCPGateway.Tests/Search/McpGatewaySearchPerformanceTests.cs`
- auto-discovery graph expansion coverage in `tests/ManagedCode.MCPGateway.Tests/ChatClient/` and `tests/ManagedCode.MCPGateway.Tests/Agents/`

## Definition Of Done

- graph-backed search is the default no-embedding path
- graph mode supports generated startup/index-build documents and file-system graph sources
- vector ranking is opt-in and falls back to graph ranking on query vector failure
- `Auto` uses vector-first ranking, preserves semantic primary ordering, and returns `hybrid` when graph supplementation contributes expansion matches
- optional English query normalization works through `Microsoft.Extensions.AI`
- tool search hints can enrich multilingual and domain-specific discovery
- multilingual, typo-heavy, focused expansion, and file-backed graph scenarios are covered by automated tests
- runtime telemetry is emitted through built-in .NET diagnostics
- docs explain how to configure graph, file-backed graph, embeddings, and optional query normalization
- build, analyzers, and tests stay green

## Related Docs

- [`README.md`](../../README.md)
- [`docs/ADR/ADR-0002-search-ranking-and-query-normalization.md`](../ADR/ADR-0002-search-ranking-and-query-normalization.md)
- [`docs/ADR/ADR-0005-markdown-ld-graph-search-for-tool-retrieval.md`](../ADR/ADR-0005-markdown-ld-graph-search-for-tool-retrieval.md)
- [`docs/ADR/ADR-0001-runtime-boundaries-and-index-lifecycle.md`](../ADR/ADR-0001-runtime-boundaries-and-index-lifecycle.md)
- [`docs/Architecture/Overview.md`](../Architecture/Overview.md)

## Implementation Plan

1. Keep search-normalization configuration in `McpGatewayOptions` and a keyed DI service key for the optional normalizer chat client.
2. Keep normalization in the search pipeline with graceful fallback and diagnostics.
3. Use `ManagedCode.MarkdownLd.Kb` as the Markdown-LD graph and token-distance search implementation.
4. Support generated and file-system graph sources in the runtime graph index.
5. Keep deterministic tests for query normalization, generated graph, file-backed graph, vector fallback, vector-first auto supplementation, telemetry, and performance smoke.
6. Update `README.md` with configuration, telemetry, and operational guidance.
