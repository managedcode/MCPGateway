# Governed execution delivery

Status: runtime implemented; final gateway verification and release in progress. Public graph owner: MarkdownLd.Kb 0.2.18. SPIN has a concrete upstream blocker.

All A-F acceptance criteria in [the implementation plan](../Plans/MCPGatewayImplementationPlan.md) remain authorized. Publishing the CLI and graph fixes did not cancel them.

## Scope

In scope: trusted execution context and consistent access enforcement; reviewed, versioned catalog publication and safe refresh; compact discovery and exact schemas; typed outcomes, projection and approved result storage; identity-specific clients, egress and bounded admission; invocation/publication/delivery telemetry; gateway full-text/SPIN integration; measured owner fallback allocation repair. Each behavior requires integration regressions and consumer documentation.

Out of scope: application authentication providers, approval UI, databases, vendor infrastructure, OpenAPI/gRPC adapters without a concrete integration, further low-priority CLI features.

## Implementation order and acceptance evidence

| Slice | Runtime contract and checks | State |
| --- | --- | --- |
| C | Summary search with byte budget and continuation; authorized complete schema lookup; schema-faithful AIFunction arguments; SDK discovery-only export | Implemented; integration verified |
| D | Serializable MCP tool-error outcomes; JSON Pointer projection; one approved outward representation; bounded host-owned result store with identity/TTL checks; safe schema handling | Implemented; integration verified |
| A | Host-provided trusted context, publication/access gate before discovery and final dispatch; no model context impersonation; graph/related/cache/prompt/resource/task boundaries | Implemented; integration verified |
| B | Immutable definitions, canonical hashes and review diffs; compare-and-swap publication; refresh preserves valid snapshot and client leases; rollback retains revocations | Implemented; integration verified |
| E | Host-owned execution client resolution; audience/credential revision partitions; explicit egress/redirect policy; bounded per-source admission and deadlines; no mutating retry | Implemented; integration verified |
| F | Bounded nonsensitive invocation/decision/refresh/delivery instruments; opt-in audit; scaling/refresh benchmarks with allocation statistics and P95 | Runtime/tests implemented; final full benchmark run pending |
| Graph | Lucene, ontology, authorized graph projection and caller-bound federated SPARQL; owner focused fallback preserves semantics and reduces measured allocations | Implemented; SPIN blocked by [dotNetRDF issue 911](https://github.com/dotnetrdf/dotnetrdf/issues/911) |

## Verification and delivery

Implement and test coherent slices in the primary checkout. Preserve unrelated owner edits. Use official SDK protocol models and host-owned infrastructure behind interfaces. Run canonical tool restore, restore, strict build/analyze, focused and full TUnit tests, formatting, Roslynator, code-size review, meaningful coverage, full BenchmarkDotNet suites and dependency vulnerability audits. Update ADR, architecture and README with actual behavior and measured performance. Deliver owned dependency fixes through their canonical release and verify the public NuGet package before consumer adoption. Verify gateway CI, release and actual public package before claiming shipped completion.

## Completion rule

A pending or partially implemented acceptance row is remaining work, not completion. Only concrete unavailable infrastructure, credentials or technical constraints can be reported as blockers. Runtime API declarations, package references, a draft patch, local test success or a pushed commit are insufficient release evidence.

## Verification checkpoints

The final strict build, formatting and Roslynator checks pass without warnings or diagnostics. All 430 integration tests pass, including the final result-cap, notification, cancellation, trusted-deadline and SDK InvalidParams regressions. The instrumented 430-test suite also passes: 92.26% line, 80.01% branch and 95.77% method coverage, above the 85% line gate. All 19 benchmark cases are undergoing final qualification; actual gateway public-package verification is still required. Owner 0.2.17 is publicly available and consumed; owner regression/coverage/full benchmarks passed. SPIN execution is intentionally absent until the official public API can support reviewed caller-controlled RDF commands.

Cold-build qualification found a further owner bottleneck: exhaustive related-segment comparisons after exact top-k choices are final. Scoped owner 0.2.17 (`e2320eed30517f0c32f936c6d707be1433b05db9`) passes 7 focused, 429 current-checkout full/instrumented tests, 100% changed-builder line coverage and all 138 local benchmark cases. Canonical release 38232018691 passed all nine benchmark suites, with 426/426 committed-source tests and 138 unique complete benchmark cases. Public signed NuGet 0.2.17 returned HTTP 200 with the exact repository SHA and matches the unsigned CI payload. Gateway adoption is complete; final full 19-case consumer qualification remains required. Unrelated owner edits are not included in that commit.
