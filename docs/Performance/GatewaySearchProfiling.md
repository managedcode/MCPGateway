# Gateway graph-search profiling — October 12, 2026

## Outcome

Gateway schema search binds candidate subjects on the original graph through the published `ManagedCode.MarkdownLd.Kb` 0.2.15 compiler. It preserves cross-tool evidence, both expansion roles, RDF types and the explicit primary candidate pool. The gateway no longer builds a reduced graph for each request or fabricates expansion from a separate ranking path.

Two measured owning-library repairs make correct full-graph retrieval practical: indexed SPARQL branches and shared evidence metadata in 0.2.14, then indexed focused URI projection in 0.2.15. On the real 120-tool catalog, uninstrumented warmed weather allocation falls from 292.917 decimal MB/op on 0.2.13 to 23.815 on 0.2.14 and 9.726 on 0.2.15. Archive falls from 304.206 to 35.403 to 21.292. This comparison qualifies the repaired full-graph path; the old reduced-graph baseline has different retrieval semantics.

## Reusable diagnostics workflow

Global `dotnet-diagnostics-mcp` 0.28.0 is installed. This task ran the requested `dotnet tool install -g dotnet-diagnostics-mcp`; the installed version was already present. The reusable skill is `/Users/ksemenenko/.codex/skills/dotnet-diagnostics-mcp/SKILL.md`, with an official MCP C# SDK JSONL helper under `scripts/invoke.cs`. The validated skill explains process selection, bounded captures, source attribution, platform limits and before/after qualification. The helper retains handles in one session and saves requests/responses in caller-owned artifacts. No global MCP configuration is changed.

`McpGatewayProfilingWorkload` uses the real 120-local-AITool `BenchmarkCatalog`, builds the production Markdown-LD/SPARQL index once, and accepts seven bounded phases. Four warmups precede each measurement; result counts and schema/fallback flags are checked on every iteration. Optional graph/query export supports source-attributed owning-library experiments. No model, embedding service or remote server participates. Real SDK MCP invocation is verified separately by integration tests.

```sh
dotnet run -c Release --project benchmarks/ManagedCode.MCPGateway.Benchmarks -- --profile
```

Send `weather 10`, `archive 10`, `exact 10`, `no-match 10`, `weather-tool 10`, `archive-tool 10`, or `no-match-tool 10` on stdin, then `exit`. An optional second argument supplies the graph-export directory. Durations must be 1–300 seconds. `GC.GetTotalAllocatedBytes(precise: true)` supplies managed bytes/op; `Stopwatch` supplies timing. The harness includes small result-validation overhead and does not replace uninstrumented BenchmarkDotNet qualification.

CPU sampling is `UnsupportedPlatform` on macOS in diagnostics 0.28.0. ClrMD preflight also reports blocked live attach, while EventPipe allocation/GC/counters succeed. No CPU findings are claimed. The advertised caller-callee view rejected its missing required argument; supported call-tree views supplied attribution. Sample frequencies are neither CPU shares nor exact byte fractions. GC aggregates cover observed valid pairs; retained details hit the 200-row cap. Weather suspension evidence is unreliable, so its elapsed collection time is not described as pause time.

## Compiler repair: actual graph and official engine

The generated export contains 56,168 triples. Initial candidate integration on published owner 0.2.13 exposed a memory regression. Official-engine BGP observation found full 56,168-row scans despite small bound VALUES inputs. Allocation responses 009–012 attribute 34.09% exclusive samples to fully-variable triple evaluation; dotNetRDF occurs on 99.32% of inclusive allocation stacks. The pure weather capture retained 54,196 allocation ticks, estimated 6.196 GB attributed bytes and 763 observed completed GCs.

Controlled query experiments preserve all 32 raw rows and reduce query allocation from 237.12 to 8.68 decimal MB/op using fixed-predicate evidence branches plus one priority-preserving label OPTIONAL. The owner implements this query compiler and constant-predicate expansion. The gateway retains the official engine and does not carry a compiler workaround. Detailed experiments, rejected shapes, thresholds and source links are in [the owner profiling report](https://github.com/managedcode/markdown-ld-kb/blob/main/docs/Performance/SchemaSearchProfiling.md).

A standalone official dotNetRDF 3.5.2 reproduction found `UsePLinqEvaluation=false` while intermediate result multisets still enable PLINQ. It is reported as [dotNetRDF issue 910](https://github.com/dotnetrdf/dotnetrdf/issues/910). No fork or replacement is introduced.

## Focused projection repair

Published 0.2.14 fixes query allocation but still scans the full asserted graph when producing a focused graph. Weather capture 014–019 attributes 59.25% of allocation samples to `BuildSchemaSearchFocusedGraph`; full asserted-triple enumeration owns 56.49%, rendering unrelated literal IDs 2.74%. The capture has 20,638 ticks, estimated 2.201 GB attributed bytes and 282 observed completed GCs.

Owner 0.2.15 selects URI focus through official subject/object indexes before rendering. It preserves object-only nodes and sorted induced edges; blank/literal focus retains the existing general RDF identity path. Read/write locking and fresh results after merge remain covered. There is no cached snapshot or custom index.

Final weather capture 021–023 uses published owner 0.2.15, gateway 0.4.19 source, .NET 10.0.12 and worker PID 28189. It retains 51,727 ticks, estimated 5.520 GB attributed bytes and 648 observed completed GCs. More operations complete during this window, so raw event totals do not quantify per-operation improvement. The focused projection subtree now has 34 of 51,727 samples (**0.0657%**); its frames use official subject indexes. Remaining weather samples include SPARQL graph-pattern variables, parsing/tokenization, binding sets and hash tables. This attribution and exact per-operation counters confirm that full graph projection ceased to be the schema hot path.

## Final warmed counters

Ten-second uninstrumented phases on the same real catalog and worker PID 28189:

| Phase | Operations | Decimal MB/op | ms/op |
|---|---:|---:|---:|
| Weather | 430 | 9.726 | 23.26 |
| Archive | 442 | 21.292 | 22.69 |
| Exact identity | 717 | 15.614 | 13.95 |
| No-match with token-distance fallback | 78 | 39.236 | 128.28 |
| Weather graph tool | 202 | 9.676 | 49.56 |
| Archive graph tool | 408 | 21.263 | 24.51 |
| Strict no-match graph tool | 3688 | 3.126 | 2.71 |

Receipt: `artifacts/profiling/gateway-api15-final-metrics.jsonl`. Timing is observational with background load; these short phases do not establish latency ratios. The normal no-match request intentionally returns five weak token-distance candidates and two related matches. The strict graph tool returns zero matches and does not use fallback. Pure profiled weather/no-match 20-second phases are recorded separately in the same receipt.

## Remaining measured fallback cost and next experiment

No-match capture 024–027 retains 28,638 allocation ticks, estimated 3.296 GB attributed bytes and 392 observed completed GCs. Its `ToSemanticSnapshot()` subtree owns **90.6313%** of sample frequency; `CreateGraphSnapshot()` owns 80.22% inclusive and 21.66% exclusive. Full asserted enumeration, node-label rendering, internal-node classification and URI segment parsing dominate. The source agrees: `KnowledgeGraph.SearchFocusedAsync` creates a complete semantic snapshot before resolving primary token-distance matches. Gateway reaches this path only after schema search and ranked candidate fallback produce no primary match.

The next owning-library experiment should resolve token-distance primary IDs first, then obtain their labels and required related/group/next-step neighborhood through official graph indexes. Preserve non-URI identities, object-only nodes, shared-group article filtering, scores, deterministic ordering and behavior after merge; compare focused results against complete snapshot behavior. Include real generated catalogs with empty, weak, multilingual and noisy queries, and retain strict zero-match schema behavior. Qualify it with full owner and gateway benchmarks, not machine-sensitive test allocation thresholds. This change is a separate retrieval redesign; the current release retains the existing fallback results. No consumer cache, hidden private index or removed fallback hides the measured cost.

## Qualification and publication

Owner 0.2.15 commit: `764a9fbc1a7cc0f47d228a4fe5a777bf1de4d876`. Local owner qualification passes 419/419 tests, module coverage 97.2295%, touched focused projection/expansion 100%, and all 131 full BDN cases in 434.31 seconds. Rich candidate matching improves 9.41 → 5.95 MiB/op; ShortDocuments 5.32 → 4.78. No-match is broadly unchanged. Local tests also include eight preserved unrelated working-tree tests.

[Canonical owner release 38191832238](https://github.com/managedcode/markdown-ld-kb/actions/runs/38191832238) succeeded. Its exact committed source passes 411/411 tests and all 131 unique full benchmark cases, validated from downloaded JSON. The existing graph-build filter additionally executes one duplicated Tiktoken-build case. Public 0.2.15 nupkg returned HTTP 200 at 01:34:54 UTC on October 12 (publish accepted 01:29:24; public blob Last-Modified 01:33:24). Consumer restore verifies the exact version and repository SHA. Receipts: `artifacts/owner-0215-build-ci.log`, `owner-0215-ci-benchmarks/`, `owner-0215-ci-receipt.json`, `consumer-restore-0215.log`.

Nine new real consumer cases verify explicit selected/empty/unknown/invalid candidate pools, cross-tool evidence, both expansion roles, Hybrid pool preservation, and official-SDK MCP search-to-invocation. Final consumer tests pass 351/351; line coverage is 92.24%, touched graph files 88.57–94.87%. Strict build/analyze has zero warnings/errors; format and Roslynator are clean. Outdated direct and vulnerable direct/transitive package lists are empty. Receipts: `artifacts/consumer-*-0215.log`, `artifacts/coverage/coverage.cobertura.xml`, and `artifacts/consumer-cloc-final.json`.

The complete nine-case old reduced-graph baseline ran in 727.4 seconds: weather 8.02 MiB/op, archive 25.22 MiB/op. It discarded cross-tool graph data and cannot establish an equivalent-retrieval speedup. Receipts: `artifacts/consumer-full-benchmark-before.log`, `artifacts/benchmarks/schema-candidate-before/results/`. Final complete twelve-case normal-job BenchmarkDotNet qualification passes in 1079.14 seconds, with all unique cases/statistics verified from full JSON. Weather allocates 9.23 MiB/op, archive 20.31, exact identity 15.03, fallback no-match 37.49, strict no-match 2.98. [Performance Benchmarks](Benchmarks.md) carries the full snapshot and timing dispersion. Receipts: `artifacts/consumer-full-benchmark-after.log`, `artifacts/consumer-full-benchmark-after-receipt.json`, `artifacts/benchmarks/schema-candidate-after/results/`. Consumer publication requires successful canonical release and actual NuGet feed visibility. BenchmarkDotNet MB uses binary MiB; warmed harness MB above is decimal.
