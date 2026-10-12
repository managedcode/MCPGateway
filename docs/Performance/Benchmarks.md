# Performance Benchmarks

`ManagedCode.MCPGateway` keeps BenchmarkDotNet benchmarks in `benchmarks/ManagedCode.MCPGateway.Benchmarks/`.

The benchmark project is not packable and is not a runtime dependency of the NuGet package. It references the package project and measures representative local gateway paths in `Release`:

- graph index build for a 120-tool catalog
- repeated schema-aware graph searches, exact tool identity and no-match/fuzzy fallback over a prebuilt catalog
- direct built-in graph tool search and strict no-match over the same prebuilt catalog
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

Latest full local BenchmarkDotNet snapshot on October 12, 2026, Apple M2 Pro, .NET SDK `10.0.401`, runtime `.NET 10.0.12`, published `MarkdownLd.Kb 0.2.15`. All 12 standard-job cases completed in 17m59s with allocation statistics; async setup, cleanup and measured paths remain async end-to-end.

| Scenario | Mean | StdDev | Allocated |
| --- | ---: | ---: | ---: |
| `BuildGraphIndex` | 824.653 ms | 179.521 ms | 490.48 MiB |
| `SearchWeatherGraph` | 18.808 ms | 16.046 ms | 9.23 MiB |
| `SearchPortfolioGraph` | 8.766 ms | 1.247 ms | 9.19 MiB |
| `SearchArchiveGraph` | 16.763 ms | 0.835 ms | 20.31 MiB |
| `SearchWeatherGraphTool` | 10.878 ms | 4.664 ms | 9.23 MiB |
| `SearchArchiveGraphTool` | 15.472 ms | 0.649 ms | 20.28 MiB |
| `SearchExactIdentityGraph` | 12.306 ms | 3.050 ms | 15.03 MiB |
| `SearchNoMatchGraph` | 76.846 ms | 20.093 ms | 37.49 MiB |
| `SearchNoMatchGraphTool` | 3.353 ms | 1.062 ms | 2.98 MiB |
| `CreateGatewayTools` | 1008.4 ns | 143.3 ns | 1.60 KiB |
| `CreateGraphTools` | 1790.3 ns | 165.9 ns | 2.84 KiB |
| `CreateDiscoveredTools` | 836.9 ns | 14.8 ns | 3.10 KiB |

Receipts: `artifacts/consumer-full-benchmark-after.log`, `artifacts/consumer-full-benchmark-after-receipt.json`, and `artifacts/benchmarks/schema-candidate-after/results/` (full JSON, CSV, Markdown and HTML). All 12 unique cases have finite positive means and nonnegative allocation statistics. Weather has a wide interval and several scenarios have substantial timing dispersion; these measurements do not establish a universal latency improvement. Allocation units here are binary MiB/KiB; the live GC-counter harness uses decimal MB.

The gateway uses a bounded candidate search index before schema-aware SPARQL on large catalogs, narrows broad queries by candidate inverse document frequency, and routes the direct graph tool through the same path as `SearchAsync`. Candidate subjects bind through owner-generated `VALUES` on the existing graph. Evidence and related/next-step expansion retain the complete graph. Owner 0.2.15 uses constant-predicate SPARQL branches, shared evidence metadata and official RDF subject/object indexes for focused URI projection. The gateway no longer reconstructs a reduced graph per request. Caller-supplied candidate pools also remain authoritative through Hybrid ranking and fallback.

Disabling Markdown-LD Tiktoken extraction and reducing related-token fanout were also measured and rejected. Extraction mode changes broke graph export and custom-document search semantics; lower related-token fanout reduced build allocation slightly but regressed large archive search/tool latency enough to fail the tradeoff.

## Runtime Notes

The project targets modern .NET, so optimization work should account for runtime improvements before rewriting clear code. Official .NET 9 and .NET 10 performance notes show continued JIT and BCL improvements around stack allocation, deabstraction, span-friendly APIs, delegate handling, array handling, and library internals. That makes broad rewrites to `struct`, `ValueTask`, `AsSpan`, compiled lambdas, or hand-rolled enumerators risky unless this benchmark suite shows a specific win in the gateway path.

Real generated Markdown and official SDK MCP regressions cover the shared-graph behavior. The old candidate-copy baseline allocated 8.02 MiB/op for weather and 25.22 for archive, but discarded cross-tool data; it is not equivalent retrieval. The initial correct full-graph integration on owner 0.2.13 allocated about 293 decimal MB/op for weather. Compiler and focused-projection repairs reduce that to about 9.7 decimal MB/op in warmed counters on published 0.2.15. [Gateway profiling](GatewaySearchProfiling.md) retains exact counter/capture receipts and comparison limits.

Normal no-match `SearchAsync` deliberately uses token-distance fallback and returns weak candidates. Its complete semantic snapshot remains a measured hot path (about 39.2 decimal MB/op), whereas the strict graph tool returns zero matches at about 3.1 decimal MB/op. Both paths now have dedicated benchmarks; their results should not be conflated. The profiling report records the owning-library follow-up experiment and required retrieval-preservation tests.

## Optimization Policy

Use these benchmarks to compare before and after changes to search, indexing, and meta-tool code. Prefer changes that reduce measured allocations or runtime in the representative scenario without making the public API or internal flow harder to audit.

Do not apply broad micro-optimizations such as converting normal reference types to structs, replacing `Task` with `ValueTask`, compiling lambdas, or rewriting loops with `AsSpan` unless the benchmark shows that the specific path benefits.

## References

- [BenchmarkDotNet diagnosers](https://benchmarkdotnet.org/articles/configs/diagnosers.html)
- [BenchmarkDotNet console arguments](https://benchmarkdotnet.org/articles/guides/console-args.html)
- [Performance Improvements in .NET 9](https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-9/)
- [Performance Improvements in .NET 10](https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-10/)
