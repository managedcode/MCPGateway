# ADR-0019: Content-addressed tool graph index reuse

## Status

Accepted for `ManagedCode.MCPGateway` 2.3.1.

## Context

A generated tool graph is a pure function of the catalog descriptors, but every gateway instance built its own. One build for a 70-tool catalog with JSON input/output schemas and search hints costs about 0.8 s and 600 MB of managed allocation; 85% of it is Markdown-LD Tiktoken extraction and token-graph construction (67,631 triples against 3,466 from rule extraction alone). Processes that host several gateways over the same catalog, such as parallel test hosts or per-tenant factory instances, repeated that work for every instance. Ten parallel hosts took 6.0 s and allocated 6.2 GB for ten identical graphs, which pushed host startup past its bound. A refreshed source whose tools did not change also rebuilt an identical graph.

## Decision

Gateway instances in one process share one immutable generated tool graph for an identical catalog.

- `McpGatewaySharedIndexPool<TIndex>` is a process-wide pool of immutable indexes addressed by a content fingerprint. `McpGatewayRuntime.ToolGraphArtifact` is the shared value: the Markdown-LD graph, its snapshot, node maps, navigation and candidate-search indexes, the lazily built Lucene full-text index, the default-profile schema validation, and graph-node-to-catalog-position slots. It holds no `AITool`, MCP client, service provider or per-instance state.
- Each runtime binds the artifact to its own catalog entries through the slots. Search maps graph nodes to that runtime's descriptors, and invocation always executes that runtime's own `AITool` or MCP client.
- The fingerprint is SHA-256 over exactly the inputs the artifact is derived from: the generated Markdown-LD documents (path, canonical URI, content) in catalog order, each entry's descriptor document, tool name, display name and description, the graph pipeline settings, and the gateway and Markdown-LD assembly versions. Descriptions, schemas, hints, descriptor length limits and catalog order therefore produce new fingerprints. Search limits and configured schema profiles stay per instance.
- Only `GeneratedToolGraph` sources participate. File-system, custom-document and JSON-LD sources and authorized-graph-factory projections keep per-instance graphs that the instance owns and disposes, because their inputs are external to the descriptors.
- Concurrent acquisitions of one fingerprint coalesce into one build. A failed build is forgotten and reported to every waiter; the next acquisition builds again. A waiter whose cancellation fires releases its lease; the last one joins the build before returning, so no build outlives every caller. A completed build without remaining leases is kept idle under the same bound.
- A lease keeps the artifact alive. When a runtime's snapshot is superseded (refresh, reconfiguration, staging rejection), its release retires the artifact if no other instance holds it. When a runtime is disposed or a per-request authorized subset is released, the artifact stays idle for reuse. At most `McpGatewaySharedIndexPool<TIndex>.DefaultMaximumIdleIndexes` (2) idle artifacts remain addressable; releasing beyond the bound retires the least recently released idle artifact. A retired artifact is removed before it is disposed, the releasing caller awaits the disposal, and a disposed artifact is never handed out.
- `McpGatewayIndexBuildResult.IsGraphIndexReused` and the index-built log report whether a build joined an existing graph.

### Ownership exception

Package caches otherwise use host-owned `IMemoryCache` through DI. The pool is process-wide because separate hosts in one process have separate containers; a host-owned instance cannot be shared by them without application wiring. The exception is bounded to immutable derived graph data: entries are reference counted, idle retention is a small fixed count, eviction and disposal are deterministic, and nothing tied to a caller, credential, tool invoker or service is retained. `IMemoryCache` does not provide lease-safe disposal or single-flight builds, and a private `MemoryCache` would still be hidden. The pool type is internal and adds no public configuration. The runtime resolves an internally registered pool from its service provider before using the process-wide instance; tests use that to isolate their build counts from parallel tests.

### Measured outcome

On Apple M2 Pro with .NET 10.0.12, the 70-tool schema catalog (`BenchmarkSchemaCatalog`, `--profile-index 5`):

| Scenario | 2.3.0 with Markdown-LD 0.2.17 | 2.3.1 with Markdown-LD 0.2.18 |
| --- | ---: | ---: |
| Cold build | 794 ms, 606 MB | 827 ms, 596 MB |
| Repeated build of an identical catalog | 847 ms, 605 MB | 7.2 ms, 9.2 MB |
| Second host while the first is alive | about 857 ms, 606 MB | 6 to 8 ms, 9.9 MB |
| Ten parallel hosts, identical cold catalog | 5,984 ms, 6,164 MB | 940 ms, 687 MB |

The per-instance remainder shrank because schema-profile validation of the gateway-owned profile is computed once per artifact (it read all 67,631 triples per instance, about 65 ms and 20 MB) and descriptor schema/definition hashes are computed when a search result first reports them (9.7 MB per build for this catalog). Cold builds are unchanged in scope; the Markdown-LD extraction cost belongs to the owning library.

## Verification

- TUnit gateway tests with isolated pools: identical catalogs build once, and the shared index exports exactly the JSON-LD and Turtle graph of an independent fresh build with the same primary search results; invocation reaches each instance's own tool; changed description, schema or hints build separate graphs; six concurrent hosts build once over one exported graph; disposing one sharing gateway keeps the other working and the graph reusable; the idle bound retires the least recently released graph; reconfiguration retires the superseded graph.
- Complete search result sets are not the equality oracle. Under concurrent load, repeated identical searches on one graph object occasionally return different secondary matches and focused-graph edge counts (observed both with and without sharing, while the exported graphs stay identical). That pre-existing query-path nondeterminism is outside this decision and needs its own investigation in the Markdown-LD/dotNetRDF query path.
- TUnit pool tests over real Markdown-LD graphs: waiting acquisitions coalesce into one build, a failed build is forgotten, a canceled last waiter joins the build and keeps its result, eviction disposes the graph, and a non-reuse release retires only after the last lease.
- BenchmarkDotNet `McpGatewayIndexBuildBenchmarks` measures cold, reused and ten-parallel-host builds; cold cases use a new catalog revision per operation.

## Consequences

- Parallel or repeated gateways over one catalog pay one graph build; the cold build cost is unchanged.
- A process may retain up to two idle graphs after their gateways are disposed. Superseded graphs are not retained.
- Reuse depends on deterministic graph generation for identical documents within one process and on SHA-256 collision resistance.

## Rollout and Rollback

Ship as patch 2.3.1. Reverting removes the pool and restores per-instance builds without public contract changes other than `IsGraphIndexReused`.
