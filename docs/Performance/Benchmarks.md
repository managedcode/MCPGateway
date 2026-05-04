# Performance Benchmarks

`ManagedCode.MCPGateway` keeps BenchmarkDotNet benchmarks in `benchmarks/ManagedCode.MCPGateway.Benchmarks/`.

The benchmark project is not packable and is not a runtime dependency of the NuGet package. It references the package project and measures representative local gateway paths in `Release`:

- graph index build for a 120-tool catalog
- repeated schema-aware graph searches over a prebuilt catalog
- direct built-in graph tool search over the same prebuilt catalog
- reusable `McpGatewayToolSet` meta-tool projection

Benchmark classes use the default BenchmarkDotNet job plus `MemoryDiagnoser`. Setup, cleanup, and measured async paths run async end-to-end without sync-over-async blocking. CI runs the complete suite with `--filter "*"` and uploads the generated reports as `benchmark-results`; release runs the same complete suite before package creation and uploads `release-benchmark-results`. Neither workflow runs a smoke-only or short-run benchmark subset.

## Commands

Run all benchmarks:

```bash
dotnet run -c Release --project benchmarks/ManagedCode.MCPGateway.Benchmarks/ManagedCode.MCPGateway.Benchmarks.csproj -- --filter "*"
```

Run focused groups:

```bash
dotnet run -c Release --project benchmarks/ManagedCode.MCPGateway.Benchmarks/ManagedCode.MCPGateway.Benchmarks.csproj -- --filter "*Search*"
dotnet run -c Release --project benchmarks/ManagedCode.MCPGateway.Benchmarks/ManagedCode.MCPGateway.Benchmarks.csproj -- --filter "*Index*"
dotnet run -c Release --project benchmarks/ManagedCode.MCPGateway.Benchmarks/ManagedCode.MCPGateway.Benchmarks.csproj -- --filter "*ToolSet*"
```

Run benchmark commands one at a time. BenchmarkDotNet generates temporary build projects under the benchmark output path, and concurrent runs of the same project can contend for generated files.

Each benchmark class uses BenchmarkDotNet `MemoryDiagnoser`, so the output includes allocation columns such as allocated bytes per operation and Gen0/Gen1/Gen2 collection counts when applicable.

## Latest Full Local Snapshot

Local full BenchmarkDotNet snapshot on May 4, 2026, Apple M2 Pro, .NET SDK `10.0.201`, runtime `.NET 10.0.5`:

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

The accepted search optimization uses a gateway-built candidate search index before schema-aware SPARQL on large catalogs, keeps the schema candidate window bounded to the requested primary result size, narrows broad schema queries by candidate inverse document frequency, and routes the direct built-in graph tool through the same path as `SearchAsync`. This removes per-query upstream ranked-search snapshot extraction and avoids the old full-graph SPARQL query path for agent-facing graph search tools while preserving graph/SPARQL as the primary retrieval signal. Focused collection and private value-type rewrites were retained only where they kept the code straightforward; a custom `AsSpan` tokenizer was measured and rejected because it increased search latency for only negligible allocation movement.

Disabling Markdown-LD Tiktoken extraction and reducing related-token fanout were also measured and rejected. Extraction mode changes broke graph export and custom-document search semantics; lower related-token fanout reduced build allocation slightly but regressed large archive search/tool latency enough to fail the tradeoff.

## Runtime Notes

The project targets modern .NET, so optimization work should account for runtime improvements before rewriting clear code. Official .NET 9 and .NET 10 performance notes show continued JIT and BCL improvements around stack allocation, deabstraction, span-friendly APIs, delegate handling, array handling, and library internals. That makes broad rewrites to `struct`, `ValueTask`, `AsSpan`, compiled lambdas, or hand-rolled enumerators risky unless this benchmark suite shows a specific win in the gateway path.

For the current graph-search numbers, the main remaining allocation shape is per-query candidate graph materialization in the schema-aware Markdown-LD/SPARQL graph path. `ManagedCode.MarkdownLd.Kb` `0.2.5` exposes `CandidateNodeIds` for ranked search options, but not for the schema search profile. The useful next optimization target is an upstream schema-search candidate filter translated to SPARQL `VALUES`, so the gateway can execute schema search against the full graph with a bounded subject set instead of constructing a candidate `KnowledgeGraph` per query. That work should replace per-query subgraph construction before considering public async contract changes, DTO-to-struct rewrites, or other micro-optimizations.

## Optimization Policy

Use these benchmarks to compare before and after changes to search, indexing, and meta-tool code. Prefer changes that reduce measured allocations or runtime in the representative scenario without making the public API or internal flow harder to audit.

Do not apply broad micro-optimizations such as converting normal reference types to structs, replacing `Task` with `ValueTask`, compiling lambdas, or rewriting loops with `AsSpan` unless the benchmark shows that the specific path benefits.

## References

- [BenchmarkDotNet diagnosers](https://benchmarkdotnet.org/articles/configs/diagnosers.html)
- [BenchmarkDotNet console arguments](https://benchmarkdotnet.org/articles/guides/console-args.html)
- [Performance Improvements in .NET 9](https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-9/)
- [Performance Improvements in .NET 10](https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-10/)
