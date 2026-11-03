# MCPGateway performance/dependency release plan

Brainstorm: [chosen direction](2026-11-03-mcpgateway-performance-release.brainstorm.md). Target current SDK line patch2.3.4 (re-evaluate if latest stable SDK changes its major.minor); CLI patch0.1.1 for the separately packaged SDK adapter; it does not embed the gateway library. No new public API or runtime dependency without existing authorization.

## Ordered work
- [x] Read root/local architecture and release policies; preserve existing tool-manifest edit; install both requested skills and fix stale benchmark policy wording.
- [x] Tool restore, package restore, strict Release build, full TUnit baseline before production changes. Track each failure and cause below.
- [x] Verify actual upstream0.3.0 NuGet package and GitHub success, update all outdated direct dependencies once to final stable versions, re-run compatibility flows.
- [x] Execute the fifteen local iterations below; the durable review records checks, the one measured repair, no-change findings and invalid evidence exclusions.
- [x] Local qualification:456 tests;92.50% coverage, touched files92–100%; format/analyzers/Roslynator/cloc/audits/final build/pack green. Independent full CI benchmark execution remains in progress; root AGENTS explicitly keeps it outside the publication gate. Read-only follow-up monitors its actual result.
- [x] Update README/architecture/ADR and durable report; increment library/CLI patches once, scoped commit4f3ebc9 and non-force push to current main.
- [x] Release41549054263 and CLI41549101070 succeeded; GitHubv2.3.4 targets4f3ebc9. Both public NuGet downloads verified, gateway DLL matches GitHub asset, CLI installed successfully. No local publish.

## Fifteen iterations
1. Baseline and dependency/compatibility measurements.
2. Cold schema-heavy graph index and retained heap.
3. Identical-catalog reuse and ten parallel hosts.
4. Pool eviction, failed/canceled builds and lease ownership.
5. Weather schema/SPARQL retrieval allocation/CPU.
6. Archive/related/next-step graph expansion.
7. No-match fallback and semantic snapshot reuse.
8. Exact-name and bounded candidate selection.
9. Meta-tool schema/JSON materialization.
10. Local/real-SDK invocation routing and result delivery.
11. Search-cache retention, options and authorization partitions.
12. Repeated refresh and retired reader/subscription cleanup.
13. Cancellation/disposal lifecycle under graph-index load.
14. Multilingual, repeated/high-cardinality and vector-opt-in regression paths.
15. Repeat/soak measurements, final diagnostics and complete release qualification.

Each iteration records its check, findings, justified repair or no-change conclusion and verification. Use BenchmarkDotNet allocation statistics to select hot-path changes and real gateway/protocol integration tests to prove semantics. Separate instrumented capture overhead from clean benchmark results. Use explicit owned PIDs and retain source/DLL identity, collector refusals, GC censoring/detail caps. No speculative ValueTask/struct/span rewrite.

## Baseline failures
Fresh baseline:452 passed,0 failed/skipped, strict Release build0 warnings/errors. Installed diagnostics client needs isolated CPM opt-out because its explicit tool SDK package version must not become a solution-level dependency; added file-local ManagePackageVersionsCentrally=false. The four direct package references were then updated once; the full updated-dependency suite also passed452/452.

## Final validation order
Focused/related TUnit flows; matched BenchmarkDotNet/diagnostics/blazing-fast-dotnet evidence; complete Release tests; coverlet/reportgenerator; scoped format then full verify; Roslynator and strict analyzer build; cloc; package/outdated/vulnerability audit; final canonical Release build and package content/version; diff/self-review; release CI/publication/feed verification. Logs/artifacts remain in this repository; no stash or scratch checkout.

UPSTREAM VERIFIED: workflow41541723386 success; GitHubv0.3.0 at5f6e556; actual0.3.0 nupkg downloaded from v3 feed, ID/version/DLL payload verified. Baseline452/452 green,0 warnings. Outdated direct versions:JsonSchema.Net9.5.0->9.6.0,ManagedCode.Communication10.3.20->10.3.21,MarkdownLd0.2.18->0.3.0,TUnit1.76.28->1.78.0. Baseline vulnerability scan clean. Meta-tool BenchmarkDotNet baseline precedes updates; wait for exit before touching loaded Release binaries.

Selected measured consumer hypothesis (iteration9): repeated default meta-tool construction costs1.6KB/861.9ns for core and2.84KB/1639.5ns for graph metadata. Cache only the fixed default products per owning factory using immutable list wrappers; custom aliases stay fresh and unbounded name caches are forbidden. Runtime CreateMetaTools must reuse its owned toolset so the public facade benefits too. Add real invocation/owner-isolation and mutation guards before changing runtime code; add a public-facade BenchmarkDotNet case before measuring.

Progress: iterations1–4 and9–12 verified;5–8 workload result validation passed, with allocation stacks predominantly in official dotNetRDF. CPU collector refused due to NativeAOT misclassification of the actual CoreCLR host; retained the refusal, no zero-CPU claim. GCv2 capture retained93collections,94intervals,0reported drops. Final real search repeat will preserve all phase stdout in one file.

Filter correction: search files are partial McpGatewaySearchTests, not classes matching each filename. The first iteration14 selector ran zero tests (exit8) and is invalid evidence. Replaced with the actual full search class, which also covers the cache-specific scenarios omitted by the earlier filename filter.

Local batch ready:bounded default metadata reuse with four real flow regressions; no other confirmed consumer defect. Final direct/transitive vulnerability/outdated reports empty. Exact library/CLI payloads verified. Keep the existing Roslynk manifest change outside the scoped commit. This working plan and brainstorm are archived under docs/Plans with the delivery receipt, using a documentation-only commit that does not republish packages.

## Delivery receipt and independent qualification

All fifteen local iterations are documented in [the review](../Performance/RuntimePerformance-2026-11-03.md). Linux CI Build and Test also passed456/456 with92.45% coverage; CodeQL is green. Main source commit4f3ebc95b3cb4a4009e9629bbc4b102257d45f99 is published as library2.3.4 and CLI0.1.1. Upstream Markdown-LD0.3.0 is public and consumed normally.

- [ ] Independent full23-case CI performance qualification41549054292. This remains explicitly pending, is not represented as a successful full run, and does not block publication per root policy. Bounded read-only heartbeat `ci-mcpgateway` checks this exact run and reports its result; it has no authority to mutate the repository. No further code changes are currently known to be required.

The automatic approval reviewer rejected an earlier heartbeat that included future repository writes/commits/pushes because that future automation scope lacked explicit authorization. The accepted replacement is read-only and time-bounded. Final receipt documentation is committed in the current authorized turn, not by a background writer.
