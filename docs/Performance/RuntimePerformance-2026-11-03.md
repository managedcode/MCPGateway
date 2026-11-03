# MCPGateway 2.3.4 performance and memory review

Date: 2026-11-03. Status: released and verified on the public feed. All fifteen local iterations and Linux functional qualification passed. The independent full CI benchmark run is still in progress.

## Scope and methodology

Update every outdated direct solution dependency to the latest stable package, consume the actually published Markdown-LD 0.3.0 repair, install the requested `blazing-fast-dotnet` and `dotnet-diagnostics-profiling` skills, and complete fifteen review/measurement/regression iterations before one patch release. Preserve the official MCP SDK, dotNetRDF, authorization partitions, live catalog publication and joined lifecycle ownership. The separate CLI adapter receives patch 0.1.1; it references the official SDK and does not embed the gateway library.

Baseline source is `9f020ff`. Local measurements used Apple M2 Pro, macOS arm64, SDK 10.0.403 and runtime 10.0.14. Artifacts remain in `artifacts/performance-review-20261103/` in this checkout. Original and final DLL hashes, workload PID and scenario identity are retained in `profile-identity.json` and `final-profile-identity.json`. Baseline build and all 452 tests passed before production changes. Updated-dependency compatibility also passed 452 tests. Each confirmed consumer optimization was tested through real gateway operations; no artificial requirement for fifteen code changes was imposed.

Clean BenchmarkDotNet runs are separate from instrumented captures. Selected jobs used two warmups, five measured iterations and one launch. The complete standard-job suite remains in independent CI, without shortcuts or any benchmark dependency in Release. Bounded local stage probes are diagnostics rather than full statistical performance qualification.

## Fifteen iterations

| Iteration | Check and evidence | Finding and action |
| --- | --- | --- |
| 1 | Strict baseline and dependency-updated full suites: 452/452 each; package/feed receipts | Four stable dependency upgrades; no compatibility repair needed. |
| 2 | `baseline-index-profile.log`, `updated-index-profile.log`: schema-heavy 70-tool cold graph and forced-GC retained heap | Published graph owner reduces cold allocation; retained footprint does not improve. No consumer-side graph workaround. |
| 3 | Same-catalog rebuild, second live host and ten concurrent cold hosts in index probe | Existing content fingerprint, single-flight and per-host invoker binding work. No speculative fingerprint rewrite. |
| 4 | `iteration04-pool-tests.log`: five pool lifecycle tests | Failure retry, canceled last waiter, idle eviction and last-lease retirement pass. Fixed idle bound remains two. |
| 5 | Weather search with validated schema/evidence and 8-second allocation/GC/counter capture | Allocation samples concentrate in SPARQL parsing/evaluation. No engine replacement or unsupported CPU claim. |
| 6 | Archive search plus direct graph tool in `final-search-profile.log` | Primary/related counts, generated schema query and next-step count validated every operation. Keep graph semantics. |
| 7 | Normal and strict no-match phases in same final profile | Normal path explicitly falls back; strict graph tool returns zero candidates. Warm snapshot behavior preserved. |
| 8 | Exact-identity phase plus large-catalog search suite | Exact result and bounded candidate behavior remain intact; no arbitrary-result cache added. |
| 9 | Matched `bdn-before-tools`, `bdn-before-facade`, `bdn-after-tools`; metadata integration tests | Reuse fixed default core/graph function metadata per owner. All three repeated default access APIs now allocate 0 B/op. |
| 10 | `iteration10-invocation-tests.log`: four native protocol/governed result cases | Real SDK result preservation, schema rejection and revocation remain intact. Two real gateway owners also execute their own cached delegates. |
| 11 | Eight authorization/publication cases plus cache scenarios in the 109-test search class | Host-owned cache sizing, rebuild invalidation and client/authorization partition behavior pass. No extra cache created. |
| 12 | `iteration12-refresh-tests.log`: seven source/HTTP refresh cases; retained metadata reconfiguration test | Existing shared refresh, timeout, recovery and shutdown work. Old core and graph functions see the new catalog. |
| 13 | `iteration13-lifecycle-tests.log`: nine reuse/disposal/task-cancellation cases | A disposed host cannot retire a graph still used by another; superseded graphs retire. No new confirmed leak. |
| 14 | `iteration14-search-suite.log`: 109 real search cases | Multilingual confidence/normalization, repeated documents, large catalogs, explicit vector mode and SPARQL pass. |
| 15 | Seven validated final search phases, three-iteration index stage repeat, complete final suite/coverage/analysis | Confirm bounded reuse and qualify the combined batch. Full CI benchmark results are tracked separately from publication. |

An initial filename-based iteration14 filter selected zero tests because the search files form the partial `McpGatewaySearchTests` class. Its exit8 report is retained as invalid evidence; the actual 109-test class subsequently passed. Early failed benchmark generation/unsupported command attempts are likewise excluded. No tests were skipped or weakened.

## Measured consumer repair

[ADR-0021](../ADR/ADR-0021-bounded-default-meta-tool-reuse.md) describes ownership. The first default list is constructed lazily; later access returns the read-only list. Custom aliases and discovered proxies remain fresh. There is no static invoker cache, arbitrary-name dictionary, retained catalog snapshot or search-result cache. DI and runtime facade toolsets may both exist; each factory holds one fixed default set.

| Repeated metadata API | Before | After |
| --- | ---: | ---: |
| Public `IMcpGateway.CreateMetaTools` | 1.66 KiB/op | 0 B/op |
| `McpGatewayToolSet.CreateTools` | 1.60 KiB/op | 0 B/op |
| `McpGatewayToolSet.CreateGraphTools` | 2.84 KiB/op | 0 B/op |
| `CreateDiscoveredTools` control | 3.10 KiB/op | 3.10 KiB/op |

Before means were 875.8 ns, 861.9 ns and 1,639.5 ns respectively. Cached access measured approximately 0.7–1.5 ns and may be optimized by inlining/benchmark loops. These numbers describe metadata access only; they do not establish an invocation or SPARQL speedup. Startup of the subsequent diagnostic host overlapped the unchanged discovered-tools control near its end; no CPU comparison is claimed for that control. The zero-allocation default cases completed before that overlap.

Regressions verify immutable lists, custom aliases, real invocation, per-gateway binding and catalog reconfiguration through previously created core and graph functions. Existing SDK/authorization/result/refresh tests protect the runtime contracts. No SIMD is added to gateway code: the selected repeated allocation has no scalar loop to vectorize.

## Cold graph and memory limits

The stage fixture has 70 tools, 249,707 generated Markdown characters, 542 graph nodes and 3,466 edges. Baseline used Markdown-LD 0.2.18; final uses public 0.3.0.

| Stage | Baseline allocated bytes/op | Final allocated bytes/op |
| --- | ---: | ---: |
| Cold gateway graph | 595,915,872 | 571,871,074 |
| Repeated identical catalog | 9,220,640 | 9,217,392 |
| Ten parallel cold hosts | 687,000,208 | 663,051,242 |

Cold graph allocation fell about 4.0%, inherited from the published owner repair. These probes have different iteration counts (baseline one, final three after warmup), so they are descriptive evidence, not statistical confidence intervals. Final cold mean was 723.8 ms, reused mean 7.40 ms and ten-host mean 867.8 ms. Tiktoken graph building still allocates about 520.5 MB/op. Full-text setup remains expensive at about 828.8 MB/op; no low-memory startup guarantee is made.

Forced-GC retained first-gateway memory was 152,699,240 bytes before and 155,631,048 bytes after (about 2.93 MB or 1.9% higher). This includes process/runtime/tokenizer state and is not isolated per-graph retained size. The update does **not** demonstrate a retained-memory improvement in this fixture. Default metadata caching trades a small fixed retained function set for removal of repeated allocations.

Final seven-phase search run completed with explicit per-operation result validation. The normal no-match path has a fuzzy fallback and the strict graph tool does not. Two-second windows and tiering make the probe unsuitable for latency ratios. SPARQL per-operation allocation remains material (roughly 3.1–21.5 MB depending on scenario); the metadata optimization does not change it.

## Diagnostics provenance and limitations

Requested server: `dotnet-diagnostics-mcp` 0.28.0. The repository-local official SDK helper retains original request/response JSON in `diagnostics-1/` and captures under `captures/`. Its build is isolated from solution central package metadata; no global MCP configuration changes are needed.

Target PID 53606 was the actual gateway benchmark host, entrypoint `ManagedCode.MCPGateway.Benchmarks`, runtime 10.0.14, gateway 2.3.3 plus the reviewed source changes and graph 0.3.0. The collector incorrectly classified this CoreCLR host as NativeAOT and refused EventPipe CPU sampling (`UnsupportedRuntime`); macOS live ClrMD attach is also unsupported. Both refusals are retained. No unavailable CPU/heap collector is treated as zero work.

The 8-second allocation capture observed 6,924 allocation events (738,683,008 sampled bytes). Byte-weighted top type was `System.Char[]`; ranked allocation call stacks include HashSet initialization, VDS trie key mapping, SPARQL tokenization, graph-pattern variables and algebra sets/joins. Stack occurrence frequency and sampled allocated bytes are different measures; no external engine defect is inferred from a hot frame.

GC measurement v2 reported 93 completed collections and 94 fully suspended intervals, zero reported dropped intervals/collections, `no-detected-loss`, normal stop and available lifetime compatibility. `GCSuspendEEStop` to `GCRestartEEStart` suspension totaled 100.1636 ms, maximum 7.2541 ms, within an 8.7343 s observed window. Legacy collection elapsed 188.6733 ms is not pause time. Summary output omits 94 interval rows; the handle retains them. Missing allocation loss metadata is unknown, not asserted zero. These are instrumented workload observations, not before/after pause improvements.

## Dependency changes

| Direct dependency | Previous | Released batch |
| --- | --- | --- |
| JsonSchema.Net | 9.5.0 | 9.6.0 |
| ManagedCode.Communication | 10.3.20 | 10.3.21 |
| ManagedCode.MarkdownLd.Kb | 0.2.18 | 0.3.0 |
| TUnit | 1.76.28 | 1.78.0 |

No new central transitive pin is introduced. Markdown-LD release 0.3.0 is verified at commit `5f6e5564d373d641f65cc50989b8e3a6f71d8bc8`, [successful release 41541723386](https://github.com/managedcode/markdown-ld-kb/actions/runs/41541723386), [GitHub tag](https://github.com/managedcode/markdown-ld-kb/releases/tag/v0.3.0), and the actual public nupkg payload. The consumer has no temporary project reference or unpublished package.

## Final verification

- Full Release TUnit:456 passed, zero failed/skipped. Coverlet also completed the full suite.
- Coverlet:11,174/12,080 covered lines (92.50%); branches 80.18%. Core factory 38/38 and graph factory 126/126 lines (100%); toolset 133/144 (92.36%); runtime file 141/153 (92.16%).
- Scoped format, full format verify, strict analyzer build and canonical final build:passed, zero warnings/errors. Roslynator:zero diagnostics.
- ReportGenerator and cloc:passed. New factory 58 C# code lines; new integration test 74. Runtime file remains 649 physical lines, within the 650 file limit. Existing test-size exceptions are unchanged.
- Final outdated direct dependency scan:none. Final direct/transitive vulnerability scan:no advisories. No new central transitive pin.
- Installed diagnostics helper:isolated build/tools-list/EOF shutdown passed without compiler warnings. CLI help passed.
- Local package payloads/IDs/versions and published graph dependency validated:library 2.3.4 and CLI 0.1.1. No public API signature or persisted format changes.

Delivery is verified at source commit `4f3ebc95b3cb4a4009e9629bbc4b102257d45f99`:

- [Release 41549054263](https://github.com/managedcode/MCPGateway/actions/runs/41549054263):success; [GitHub v2.3.4](https://github.com/managedcode/MCPGateway/releases/tag/v2.3.4) targets the exact source commit.
- [CLI publication 41549101070](https://github.com/managedcode/MCPGateway/actions/runs/41549101070):success for `cli-v0.1.1`; actual public installation, version and help passed.
- [CodeQL 41549054261](https://github.com/managedcode/MCPGateway/actions/runs/41549054261):success.
- [CI 41549054292](https://github.com/managedcode/MCPGateway/actions/runs/41549054292):Build and Test succeeded,456/456 executed/passed, zero skipped; Linux line coverage11,168/12,080 (92.45%). The Full Benchmarks job125634627244 remains in progress with all23 standard cases configured. This is **not yet complete performance qualification**.
- Actual NuGet v3 flat-container downloads returned200 for library2.3.4 and CLI0.1.1. Both nuspec repository commits match the source commit. The gateway DLL bytes match the GitHub release artifact, and its nuspec references published Markdown-LD0.3.0. Receipts and hashes are in `artifacts/performance-review-20261103/published/nuget-verification.json` and `delivery-receipt.json`.

Publication is independent of benchmarks by root policy. A bounded read-only heartbeat observes the exact CI run and will report completion, failure or invalid benchmark evidence; it cannot change files, commit, push or release. It ends after reporting or at2026-11-03 03:30UTC. The existing unrelated `.config/dotnet-tools.json` edit remains visible and outside this release scope.
