# Graph consumer integration review

Scope: the gateway integration with published `ManagedCode.MarkdownLd.Kb 0.2.15`, not the separate A-F execution/publication/discovery roadmap. The gateway repair is `0.4.21`; the owner package stays unchanged.

## Findings and repairs

| Reproduced defect | Resulting behavior |
| --- | --- |
| Hybrid restored tools rejected by required types, excluded types or facets, including an empty SPARQL primary result. | Configured primary structural constraints keep the SPARQL primary result authoritative. Unconstrained ranked merging and fuzzy fallback cannot bypass them. Configured expansion is preserved. |
| Above 32 tools, automatic tool candidate selection erased ontology classes/properties from a configured general graph profile. | Automatic tool pools apply to the default tool profile. Configured graph profiles without an explicit pool search the whole graph; explicit absolute candidate IRIs and exact tool identities retain their behavior. |
| Federation compared entire absolute URLs without respecting path/query casing. | Ordinal comparison of normalized absolute URLs preserves path/query casing. .NET normalizes scheme/host. A differently cased path or parameter is blocked before execution. |
| Schema inspection omitted constraints and multi-hop relationship paths used by runtime SPARQL. | Additive properties expose excluded types, facets, configured candidate pools and predicate paths through the graph API and schema meta-tool. Null candidates remain absent in SDK JSON according to its canonical serializer policy; empty candidates serialize as an empty array. |

An empty selected federation endpoint set produces `graph_federation_no_endpoints` and no query. Retrieval constraints are not an authorization boundary. Structural constraints may reduce noisy-query fallback recall; they deliberately take precedence over unconstrained fallback.

## Verification map

| Surface | Evidence |
| --- | --- |
| Required type, excluded type, facet and nonempty facet subset | `McpGatewayGraphProfileBoundaryTests`: real generated Markdown-LD graph, direct SPARQL versus normal Hybrid results. The original negative boundary tests failed before the repair. |
| Candidate pool, predicate path and schema constraints | Public schema result and actual SDK-created `AIFunction` JSON assertions, covering null/empty/selected pools. |
| Ontology on small and large catalogs | `McpGatewayOntologyIntegrationTests`: generated 1/40-tool graphs and identical primary URI sets from gateway and published owner APIs, including an explicit ontology candidate pool. |
| Remote federation | `McpGatewayRemoteFederationTests`: ephemeral loopback Kestrel endpoint executes actual incoming SPARQL with official dotNetRDF and writes official SPARQL JSON. Remote URI/evidence remains remote, `ToolMatch` is null, and ordinary local search produces zero endpoint requests. |
| Cross-tool evidence, overlapping expansion and MCP invocation | Existing `McpGatewaySchemaCandidateFlowTests` exercises the actual owner graph and official MCP SDK client/server flow. |
| File-backed/owned graph, embeddings, errors and protocol export | Full existing gateway TUnit suite; vector tests explicitly register deterministic embeddings. |

Final gates: warning-as-error analyzer build, full TUnit suite, canonical format check, Roslynator, code-size audit, Coverlet library line threshold, package advisory/update audit, and canonical CI/release full BenchmarkDotNet suite. Public NuGet verification must match the release version, repository commit and package entries; NuGet's added repository signature is expected.

Local result: 366/366 tests pass, including 15 new cases. The analyzer build has zero warnings/errors; Roslynator has zero diagnostics. Coverlet reruns all 366 tests and measures 92.27% library line coverage, 79.46% branch coverage and 95.41% method coverage. All four projects have no vulnerable packages or available direct package updates in the NuGet audit. New test files stay below their 400-line file and 300-line type budgets; the existing runtime-partial exception is recorded in ADR-0012.

## Capability boundaries

The shipped gateway exposes schema-aware SPARQL, profile inspection, graph/index tooling, complete-graph evidence/expansion, ontology profile search, explicit federation and graph export. Normal search remains graph-first, with explicitly selected embeddings and existing ranked/token support.

Lucene full-text indexing/search exists in the owner package but has no dedicated gateway adapter. Official SPIN packages are retained by the owner but no new SPIN execution adapter is claimed. These are feature additions, not completed gateway integrations. The broader A-F roadmap remains in `docs/Plans/MCPGatewayImplementationPlan.md`.

The previous profiling result still identifies whole-graph semantic snapshot construction in the token-distance no-match fallback as a remaining allocation hotspot. This review preserves that fallback's behavior; it does not claim the allocation issue is resolved.
