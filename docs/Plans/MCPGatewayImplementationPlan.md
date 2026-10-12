# MCPGateway: governed execution and progressive discovery

Status: A-F design retained; implementation deferred by the user. Graph-search and profiling implementation is delivered in gateway 0.4.19; deterministic CI coverage repair is qualified for 0.4.20 and tracked by its canonical release/public feed.

Review date: 2026-10-11

Target repository: `managedcode/MCPGateway`, public `main` as retrieved during this review. The inspected `Directory.Build.props` declares package version `0.4.18`. [S18] Local implementation baseline: `f2f432a2c39e15ecd50035d71262f7761a98a079`. This review used public source files and project documentation. Original review was read-only. Current baseline: Release build passed with 0 warnings/errors; 342/342 TUnit tests passed. The fresh full nine-case BenchmarkDotNet baseline completed in 727.4 seconds; historical numbers in the original assessment are retained as historical context.

All new type names, commands, options, and file paths below are proposals unless explicitly identified as existing.

## 1. Decision

Extend the reusable .NET library with a consistent policy-aware execution boundary, versioned publication, compact discovery, schema-faithful discovered tools, and safe result delivery. Preserve the existing graph-first retrieval and official MCP SDK integration. Keep application UI, identity-provider operations, approval UX, payment providers, and distributed persistence in consuming hosts.

Uber's article supplies useful patterns: reviewed publication, separation of registry management from runtime execution, incremental discovery, response projection, API adapters, and a CLI path for coding agents. Its internal service infrastructure is outside this plan. [S1]

### Existing foundations

The architecture already separates the runtime facade, mutable registration, full catalog reconfiguration, graph search, prompts, resources, and factory-created instances. The implementation has immutable search snapshots, lazy index construction, and optional warmup. Extend those boundaries. [S2]

`WithMcpGatewayCatalog()` already exports aggregated MCP capabilities, and `IMcpGatewayServerBindingResolver` provides a request-specific binding seam. The current host can select a gateway for a route, tenant, or authenticated request. Use that seam for a multi-application host. [S3]

`McpGatewayToolSet` already provides search, route, and invoke meta-tools. New discovery features should reuse them. [S4]

## 2. Findings from inspected code

| Finding | Evidence | Consequence for this plan |
| --- | --- | --- |
| Discovery enablement is a separate concern from authorization. | `McpGatewayToolDescriptor.IsEnabledByDefault` defaults to true; the documented flag controls search visibility. The inspected `InvokeAsync` resolves a target, prepares arguments, and dispatches. [S5, S6] | Preserve the visibility hint and introduce an explicit authorization/publication gate. Validate every execution entry point. |
| Discovered proxy schemas are generic. | `CreateDiscoveredTool` constructs a function with an `arguments` dictionary and contextual parameters. It does not assign `match.InputSchema` as the function schema; its description provides textual hints. [S4, S7] | Add a schema-preserving proxy mode with a compatibility path. |
| Search matches contain schema payloads. | `McpGatewaySearchMatch` includes `InputSchema` and extensive metadata. [S8] | Add a compact discovery result contract and a separate authorized schema lookup. |
| Execution success and tool outcome need separate representation. | `InvokeMcpToolAsync` constructs a successful invocation result for a completed MCP call; the complete MCP result is retained separately. `McpResult` is JSON-ignored. [S6, S9] | Add a serializable outcome that distinguishes protocol completion from tool-level errors, preserving existing compatibility. |
| Reconfiguration is process-local runtime mutation. | `McpGatewayRegistry.ReconfigureAsync` replaces registrations, increments an internal version, and disposes previous registrations. [S10] | Add durable-definition and publication contracts above this mechanism. Investigate in-flight lifetime semantics before using it for zero-interruption publication. |
| Built-in telemetry focuses on search and indexing. | `McpGatewayTelemetry` defines search/index activities and instruments. [S11] | Add bounded invocation, policy, output-delivery, and source-refresh observations. |
| There is a documented allocation target. | The performance guide identifies candidate-subgraph materialization and proposes a schema-search candidate filter translated into SPARQL `VALUES`. [S12] | Validate that hypothesis on the pinned baseline; coordinate any required Markdown-LD dependency change. |

These findings describe inspected code paths. They do not establish the security behavior of every consuming application or every uninspected extension.

## 3. Architecture and compatibility rules

Retain the package-first scope, .NET/C# foundation, `Microsoft.Extensions.AI`, official MCP SDK, graph-first discovery, and TUnit workflow. New public members should normally be additive. Review positional records carefully: adding an optional positional parameter changes the compiled constructor signature. Prefer init-only properties, compatible overloads, or separately versioned contracts. [S13]

Keep policy execution in reusable library code and integrate the host's identity/authorization providers through explicit contracts. Do not bind core contracts to `HttpContext`, a particular authentication scheme, a database, Orleans, an agent framework, or a cloud vendor.

### Proposed responsibility map

| Feature slice | Responsibility |
| --- | --- |
| `Invocation/` | Execution pipeline, effective argument validation, outcome classification, deadline/admission policy, safe response processing. |
| `Catalog/` | Source/tool identities, approved definitions, publication snapshots, schema drift, source refresh. |
| `Discovery/` | Compact summaries, schema lookup, staged meta-tools, schema-faithful discovered proxies. |
| `Search/` | Authorized candidate universe, policy-scoped caching, graph filtering, existing ranking behavior. |
| `Hosting/` | SDK handler integration, authenticated request binding, export modes, task/resource/prompt enforcement. |
| `Gateway/` | Thin orchestration, DI wiring, bounded telemetry, shared serialization. |
| Optional sibling projects | OpenAPI adapter and CLI; gRPC adapter after a concrete integration requirement. |

Use one shared execution path across local functions, MCP tools, discovered proxies, and future API adapters. Keep discovery/catalog management operations on their appropriate public interfaces.

## 4. Workstream A: execution boundary and trusted request context

### A1. Trusted context

Propose an immutable `McpGatewayExecutionContext`, created by the host after authentication. It should carry an internal principal reference, tenant/workspace/application identity, caller kind, authorization partition, policy revision, correlation identifier, and effective deadline. A host may support an explicitly configured trusted-local principal.

Existing model-supplied `Context`, `ContextSummary`, query, tool arguments, and upstream `_meta` remain untrusted application data. They must never populate principal identity, role membership, tenant entitlement, or approval state. Context forwarded to upstream tools should have a separate allowlist and should exclude credentials.

Avoid capturing scoped user context in singleton gateway registrations. Pass trusted context through an explicit per-operation binding or through request-scoped collaborators selected by the host. Test parallel operations with distinct identities.

### A2. Authorization and publication enforcement

Propose `IMcpGatewayAccessPolicy` with batch discovery filtering and per-operation decisions. Operations should distinguish discovery, schema inspection, invocation, resource read, prompt render, graph administration, and task/result access. The implementation should be reusable across transports.

Evaluate authorization and publication state immediately before dispatch. Argument-dependent policy must inspect the final validated arguments after convenience mapping. Introduce a restricted execution stage that cannot be skipped by configurable middleware.

Apply the same effective access rules to list/search/route, related and next-step matches, graph evidence/export, schema lookup, direct tool calls, generic invoke meta-tools, discovered proxies, prompts, resources, completion, MCP App resources, tasks, and artifact retrieval. Hide forbidden object existence according to a consistent external error policy.

Authorize the candidate universe before ranking and expansion where private tool metadata can affect evidence or external query processing. Partition all metadata/result caches by the appropriate visibility and identity boundaries. Graph federation must have an explicit query/data egress policy in addition to endpoint configuration.

Keep privileged graph export and index rebuild tooling out of the normal end-user discovery profile. An explicit administrative profile may expose them.

### A3. Lifecycle semantics

Retain `IsEnabledByDefault` as a discovery preference. Introduce a separate managed lifecycle, for example `Draft`, `Approved`, `Published`, `Disabled`, `Deprecated`, and `Quarantined`. Keep source health orthogonal to lifecycle.

The existing trusted, code-registered local-tool path should retain its documented default behavior. Imported or remotely discovered definitions in managed mode start unpublished. A host enabling managed mode must register its policy and publication providers; missing dependencies fail startup.

A tool hint such as `ReadOnlyHint`, `DestructiveHint`, or `IdempotentHint` is advisory metadata. Host-approved policy determines whether execution, confirmation, or retry is allowed.

**Acceptance:** deny every alternate invocation path for an unpublished tool; block a known tool ID after revocation; reject a forged tenant/role inside `Context`; prove that hidden tool names and descriptions do not appear in related matches, graph evidence, schema responses, prompts, resources, or shared caches; prove bounded cleanup of per-caller state.

## 5. Workstream B: versioned publication and source refresh

### B1. Definitions and publication

Propose `IMcpGatewayCatalogDefinitionStore`, immutable `McpGatewayCatalogDefinition`, and `McpGatewayPublishedSnapshot`. Define storage-independent contracts; a host may supply an in-memory, file, relational, or distributed implementation. Do not introduce a mandatory persistence package.

Track source ownership, tool stable identity, upstream binding, requested permissions, public description, schemas, definition revision, publication state, reviewer reference, and provenance. Keep secrets outside definitions; store secret references only.

Maintain both a schema hash and a complete definition hash. The latter must include description, security-relevant annotations, backend destination/binding, required permissions, and other approved metadata. Normalize only semantically unordered structures when hashing.

Produce an explicit diff between candidate and published definitions. Require review for newly discovered tools, schema changes, security-significant description changes, endpoint changes, and permission changes. AI-generated descriptions may enter a draft workflow through an optional host service; publication remains explicit.

### B2. Safe runtime publication

Validate the candidate, build the required index, and publish one immutable runtime snapshot through an atomic switch. Keep the previous working snapshot when candidate validation or index construction fails. Add a host-visible publication result and revision.

Provide compare-and-swap/expected-revision semantics for concurrent publishers. Separate content rollback from current security revocations: rolling back a catalog must never silently restore revoked authorization or disabled dangerous tools.

Before reusing `ReconfigureAsync`, cover resource/client ownership during concurrent invocation and cleanup. If necessary, add a snapshot lease or equivalent lifetime mechanism so in-flight work releases its previous binding safely. Define whether disabling a tool blocks new dispatch only or also requests cancellation of existing work; document the limitations of cancelling downstream side effects.

Preserve canonical tool naming. Add tests for newly introduced name collisions. Persist or otherwise stabilize source/tool-to-public-name assignments in managed catalogs when changing membership would rename an already published tool. Any incompatible rename requires an explicit migration decision.

### B3. Targeted refresh

Add source-scoped refresh, a change result, and a configurable refresh policy. Reuse supported upstream SDK change signals when applicable; use host-configured periodic refresh for transports/capabilities without such signals. Do not invent a second MCP notification protocol.

Refresh configured sources only. Use bounded concurrency, single-flight per source, cancellation, source fingerprints, and backoff. Compute a candidate diff and leave new definitions unpublished. Preserve unchanged connections and reusable embeddings. Reuse unaffected search structures where correctness permits.

A refresh timeout is a health event. It should not automatically delete approved catalog entries. Explicit source removal, stale-schema policy, and quarantine require separately defined rules.

**Acceptance:** a failed refresh preserves the published snapshot; schema drift requires approval; concurrent publishers receive revision conflicts; unchanged sources do not reconnect; new tools stay unavailable until publication; rollback preserves current revocations; no client is disposed while an authorized in-flight operation still owns its binding.

## 6. Workstream C: progressive discovery and schema-faithful tools

### C1. Compact discovery

Keep the existing search, route, and invoke tools. Add an optional `gateway_tool_schema` tool and, for large installations, `gateway_sources_search`. Separate source routing from domain/category routing.

Propose `McpGatewayToolSummary` containing stable tool ID, source identity, concise purpose, approved execution hints, revision, and schema hash. Add summary/full detail selection through a compatible API. Provide source/category filters and bounded continuation where needed.

Budget aggregate serialized bytes for primary, related, and next-step results. Full schemas remain complete valid JSON when returned; oversized schemas should use an explicit response or bounded resource retrieval. Arbitrary string truncation is excluded. Token budgets require an explicitly selected tokenizer; serialized byte counts are always separately reported.

Typical proposed sequence:

```text
search summaries -> inspect selected schema -> invoke selected tool
```

Allow direct calls when the caller already holds an authorized, current schema. Do not force an extra discovery round trip for every invocation.

### C2. Schema-preserving discovered proxies

Provide a discovered-tool mode whose `AIFunction` schema reflects the selected tool's real arguments, nested structures, required fields, enums, and additional-property constraints. Use the supported `Microsoft.Extensions.AI` extension surface and the repository serializer.

Keep trusted context outside the model-visible argument schema. Route the function's arguments through the shared execution boundary. Preserve canonical gateway identity and the approved description/annotations.

Retain the current generic wrapper mode for compatibility. Mark unknown or unavailable schemas explicitly. A lookup required to obtain a full descriptor must pass the same authorization rules as schema inspection.

### C3. MCP export modes

Add an opt-in discovery-only export mode that lists the bounded meta-tools and uses the same gateway for execution. Keep the existing catalog export mode as the compatibility default. A host may expose separate catalog/discovery endpoints or choose a documented mode per binding.

Use official SDK models and handlers. A compact discovery export must not also enumerate the entire underlying tool set unless explicitly configured.

**Acceptance:** a cold discovery endpoint lists a fixed small meta-tool set; summary results omit full schemas; schema lookup returns exact constraints and revision; discovered proxy schema tests cover nested objects and arrays; revoked tools cannot be invoked through previously materialized proxies; a 5,000-tool fixture respects the configured discovery payload budget without hiding relevant authorized matches.

## 7. Workstream D: outcomes, response projection, and safe result delivery

### D1. Serializable invocation outcome

Add an explicit outcome such as `Succeeded`, `ToolError`, `Denied`, `InvalidArguments`, `RateLimited`, `TimedOut`, `Cancelled`, `UpstreamUnavailable`, and `UnknownCompletion`. Retain existing `IsSuccess` compatibility until a deliberate versioned API change.

Expose a safe error code and, when justified, a retry-after hint. Keep MCP transport errors distinct from tool-level `IsError`. Ensure generic meta-tool serialization communicates the tool outcome even though the full MCP result remains a separate protocol-facing property.

Use cancellation propagation rules already established by the package. Treat a timeout after dispatch to a mutating backend as potentially having an unknown outcome; cancelling a local await does not prove that the remote operation was reversed.

### D2. Response options

Propose `McpGatewayResponseOptions` as an init-only request property or separate compatible overload. Gateway-owned options include field selection, maximum delivered bytes, structured-output preference, and optional artifact delivery. These options must be separated from downstream business arguments.

For an initial field selector, use deterministic JSON Pointer-style paths with explicit array indexes. Any wildcard or collection projection syntax requires a separately documented grammar, bounded traversal, and tests. Do not execute arbitrary filters or code.

Example of a proposed gateway wrapper request:

```json
{
  "toolId": "orders_get",
  "arguments": { "orderId": "A123" },
  "responseOptions": {
    "selectPaths": ["/orderId", "/status", "/tracking/url"],
    "maxBytes": 16384
  }
}
```

Local projection reduces delivered content. It reduces upstream transfer only when an adapter explicitly supports safe field-selection pushdown.

### D3. Privacy and MCP representation consistency

Propose an `IMcpGatewayResultPolicy` for access-controlled redaction, classification, validation, and delivery. Build one approved outward result. Ensure `Output`, MCP structured content, text content, metadata, error content, resource links, and task/artifact delivery cannot expose an unprocessed alternate representation.

Do not keep an unsanitized MCP result on a caller-visible object when an output policy is mandatory. Upstream payloads may be transient internal values only. If the host permits raw audit storage, define that as a separately authorized, explicit capability with retention controls; default behavior stores no raw payload.

Projection can remove fields required by the original output schema. Preserve original contracts on transparent export when policy permits. Expose projected responses through the gateway invoke envelope with its own public output contract. If mandatory redaction makes a typed direct-tool response invalid, use an approved schema-safe representation or a safe error. Never advertise an output schema that the delivered result violates.

Preserve images, audio, resources, MCP App metadata, and other non-JSON content when permitted. Do not flatten all blocks into a string. When a configured privacy policy cannot safely process a content type, fail closed or require an explicit host adapter. Validate the final outward response before delivery.

### D4. Large outputs

Propose host-owned `IMcpGatewayResultStore` with authenticated retrieval, TTL, byte/item limits, cleanup, and explicit ownership. Store the approved outward result. Return a compact preview and an opaque result/resource handle.

Authorize every read using trusted identity and current policy. An opaque handle alone confers no access. Keep arbitrary server filesystem paths out of model-supplied arguments. Apply the same rules to task results and asynchronous delivery.

**Acceptance:** selected fields are returned; raw alternate MCP fields contain no seeded secret; model-visible options are absent from upstream arguments; `IsError` survives meta-tool serialization as an outcome; output schema validation passes; oversized JSON remains structurally valid; cross-user artifact reads fail; expired results disappear; cancellation and unknown mutating outcomes are distinguishable.

## 8. Workstream E: identity delegation, network safety, and resilience

### E1. Identity-aware upstream binding

Extend the existing host binding seam with a credential/client resolver that uses trusted tenant, subject, source, audience/resource, scopes, consent, and credential revision. Separate shared technical discovery credentials from end-user execution credentials where the upstream supports that pattern. A catalog itself may be user-specific.

Use the official SDK's supported OAuth integration and host-owned credential storage. The gateway validates the credential intended for itself; upstream calls use appropriately issued credentials. Implement token exchange only when the issuer and downstream support it. OAuth audience validation and prevention of unchecked token forwarding are explicit MCP security requirements. [S15, S16]

Do not change shared client authorization headers per request. Any pooled state must include its full security partition and bounded lifetime. Avoid retaining the entire catalog/index per user when the metadata is demonstrably identical and safely shareable.

### E2. Outbound access

Require explicit source/endpoint policy for remote imports and multi-user hosts. Cover initial URLs, redirects, OpenAPI remote references, OAuth metadata endpoints, and federated graph endpoints. Apply appropriate network egress enforcement, DNS/redirect controls, and explicit private-network exceptions for configured internal services. MCP guidance identifies these SSRF paths. [S15]

Do not allow a model to supply arbitrary stdio commands through normal discovery tools. Preserve explicit administrator registration and least-privilege process environment configuration.

### E3. Admission and resilience

Propose configurable invocation deadlines, per-source concurrency limits, bounded queues, circuit breaking, and a quota-provider interface. Use host-owned distributed coordination where cross-replica quotas are required; document the scope of process-local limits.

Default retry behavior for tool execution should be conservative. Permit automatic retries only when the host-approved contract and backend support make them safe. An upstream idempotency hint is insufficient evidence. Durable deduplication requires a real backend idempotency contract and appropriately scoped keys; the gateway cannot promise exactly-once external side effects.

Avoid retry multiplication across host, gateway, SDK, and HTTP layers. Do not count user cancellation or ordinary business validation failures as source-health failures. Keep discovery refresh budgets separate from execution budgets.

**Acceptance:** concurrent users never share credentials; wrong-audience credentials are rejected by the relevant authentication boundary; a revoked credential is no longer reused; forbidden redirects and remote references are blocked; queue/connection stores remain bounded; mutating timeout tests produce no blind retry; per-source failures do not exhaust unrelated sources.

## 9. Workstream F: observability and measurable scaling

Add invocation activity and metrics for dispatch duration, outcome, admission rejection, source refresh, upstream/delivered bytes, projection, and artifact usage. Keep default metric labels bounded and non-sensitive. Do not use arbitrary tool IDs, tenant IDs, argument values, endpoint strings, or exception messages as unrestricted metric labels.

Preserve the existing safe-runtime-diagnostics policy. A separate opt-in audit sink may receive host-approved pseudonymous actor/asset references, decision code, policy/catalog revision, and timing under explicit privacy controls. Do not silently broaden normal logs. [S14]

Track model input/output tokens only when actually measured by a selected tokenizer/provider. Mark inferred token savings and monetary cost as estimates with a declared method.

### Benchmark plan

Reproduce the current baseline with pinned SDK, package versions, commit, machine, and dataset. The performance guide describes a 120-tool benchmark fixture and reports a May 4, 2026 local snapshot, including `25.88 MB` allocated by `SearchWeatherGraph` and `500.46 MB` by `BuildGraphIndex`. These are historical measurements; they are not fresh results from this review. [S12]

Add datasets with 1,000, 5,000, and 10,000 tools; varied schema sizes; source failures; enabled/disabled mixtures; multiple permission partitions; concurrent callers; and repeated source updates. Measure cold/warm discovery, schema lookup, invocation overhead, tail latency, allocations, retained memory, index work, and delivered discovery bytes.

Investigate the documented candidate-subgraph allocation hypothesis. First verify whether the pinned `ManagedCode.MarkdownLd.Kb` exposes schema-search candidate filtering. If needed, implement a focused upstream capability that binds authorized candidate IDs through SPARQL `VALUES`; then consume it in MCPGateway. Preserve exact-ID, graph relationship, custom graph binding, and federation behavior. Benchmark full schema search behavior. Retain the graph/SPARQL retrieval contract.

Set regression thresholds from a reproducible baseline. Record any proposed latency or allocation target as an acceptance target, never as a claimed existing result.

## 10. Optional adapters and coding-agent CLI

### OpenAPI adapter

Create a separate optional `ManagedCode.MCPGateway.OpenApi` project after the execution/publication boundary exists. Parse a configured OpenAPI document into draft descriptors and callable `AIFunction` adapters. Feed them into the existing tool catalog and execution pipeline.

Support a deliberately specified OpenAPI subset: stable operation identity, path/query/header/body serialization, schema resolution, security requirements, response content handling, and operation-level allowlists. Reject unsupported constructs clearly. Define remote-reference and destination policy. Keep authentication headers host-owned.

Start with approved read operations. An HTTP method alone does not establish application safety. Add write operations after policy, validation, outcome, and idempotency behavior are covered. No server-wide automatic publication after import.

### gRPC adapter

Add only for an identified consumer/service. Prefer configured descriptor sets and unary calls initially. Reflection is an explicit trusted-source option. Address protobuf field naming, presence, enums, `oneof`, 64-bit numbers, bytes, well-known types, status mapping, and deadlines. Defer streaming until a concrete MCP result/resource/task mapping is specified.

Thrift, TChannel, a service mesh, and a distributed crawling platform are outside the initial roadmap.

### CLI

Create a separate .NET tool project with proposed commands:

```bash
mcpgateway sources list
mcpgateway tools search "find failed deployments" --json
mcpgateway tools schema deployments_get
mcpgateway tools call deployments_get --arguments-file request.json --output result.json
```

Use the same authentication, policy, schema, outcome, and invocation contracts. The CLI writes an explicitly requested local file; the server does not interpret that path. Keep stdout machine-readable, diagnostics on stderr, stable documented exit codes, cancellation, and an explicit overwrite policy. Credentials must stay out of shell history and process arguments where feasible.

This CLI has no arbitrary code-execution endpoint. Coding agents may compose their own local shell pipelines subject to their host's permissions.

## 11. Pull-request sequence

Each pull request contains its feature behavior, relevant tests, docs, compatibility notes, and measured impact where applicable.

| PR | Priority | Deliverable | Dependency and completion gate |
| --- | --- | --- | --- |
| 01 | P0 | Baseline pinning, threat model, compatibility and behavior characterization | Read current tests; add regressions for identified execution/schema/outcome boundaries; record the current benchmark baseline. |
| 02 | P0 | Trusted context and access policy | PR01; all alternate access paths and cache partitions covered. |
| 03 | P0 for shared hosts | Identity-aware upstream credentials and outbound-access policy | PR02; cross-user isolation and network-policy integration tests pass. |
| 04 | P0 | Outcome contract and invocation observations | PR01/02; serialized meta-tool results preserve tool errors; privacy tests remain green. |
| 05 | P1 | Managed lifecycle and versioned publication | PR02; reviewed definitions, expected revision, safe publication, and rollback behavior implemented. |
| 06 | P1 | Compact discovery, authorized schema lookup, schema-preserving proxies | PR02/04; compatibility mode and bounded remote discovery export tested. |
| 07 | P1 | Result policy and response projection | PR02/04; all representations and output-schema compatibility tested. |
| 08 | P1 | Large-result store integration | PR07; identity-bound handles, retention, byte limits, and task/result access tested. |
| 09 | P1 | Targeted refresh and runtime lifetime hardening | PR05; unchanged bindings retained, failed candidates preserve current publication. |
| 10 | P1 | Admission, deadlines, safe resilience | PR02/03/04; queue isolation, cancellation, and mutating timeout semantics tested. |
| 11 | P1, parallel investigation | Large-catalog benchmarks and SPARQL candidate-filter integration | PR01; profiling evidence and required upstream change, followed by quality/performance comparison. |
| 12 | P2 | Optional OpenAPI adapter | PR03/05/07/10; allowlisted import, reviewed publication, and real HTTP integration tests. |
| 13 | P2 | Optional CLI | PR03/04/06/07; real CLI process tests, credentials, JSON output, local files, and exit status. |
| 14 | Conditional | Optional gRPC adapter | Identified consumer plus mature execution pipeline; descriptor and real gRPC integration tests. |

The first useful release should combine PR02, PR04, PR06, and PR07 with PR01 characterization. A shared multi-user deployment also requires PR03. Managed import/publication requires PR05 before broadening discovery.

## 12. Suggested specification and test files

Add feature specifications, using English repository documentation:

```text
docs/Features/GovernedToolExecution.md
docs/Features/VersionedCatalogPublication.md
docs/Features/ProgressiveDiscovery.md
docs/Features/ResponseProjectionAndResultDelivery.md
docs/Features/IdentityAwareUpstreamBindings.md
docs/Features/SourceRefreshAndResilience.md
docs/Features/OpenApiToolAdapter.md
docs/Features/GatewayCli.md
```

Allocate the next available ADR numbers after inspecting the pinned repository. Do not overwrite existing decisions. Update `docs/Architecture/Overview.md`, README, and the relevant project-local guidance for changed boundaries.

Suggested new test suites:

```text
Invocation/McpGatewayAuthorizationTests.cs
Invocation/McpGatewayOutcomeTests.cs
Invocation/McpGatewayResponseProjectionTests.cs
Invocation/McpGatewayResultPolicyTests.cs
Invocation/McpGatewayResilienceTests.cs
Catalog/McpGatewayPublicationTests.cs
Catalog/McpGatewaySourceRefreshTests.cs
Discovery/McpGatewayProgressiveDiscoveryTests.cs
Discovery/McpGatewayDiscoveredToolSchemaTests.cs
Hosting/McpGatewayIdentityIsolationTests.cs
Hosting/McpGatewayPolicySurfaceTests.cs
Hosting/McpGatewayResultHandleAuthorizationTests.cs
```

Keep names aligned with the actual test topology when implementing. Reuse existing privacy and protocol integration fixtures.

## 13. Definition of done

Every changed execution path has meaningful TUnit regression coverage. Protocol-dependent behavior uses actual official-SDK client/server integration. Tests cover denial, error, cancellation, stale schema, concurrent update, source failure, credentials, and cleanup as relevant.

Builds and analyzers are warning-clean; formatting, architecture documentation, package compatibility, and dependency-vulnerability checks pass. Run the complete benchmark suite for performance/release verification. Keep dependency versions centralized and keep SDK protocol negotiation/transport behavior intact. [S13, S17]

Core verification commands, recheck against the pinned repository before execution:

```bash
dotnet tool restore
dotnet restore ManagedCode.MCPGateway.slnx
dotnet build ManagedCode.MCPGateway.slnx -c Release --no-restore
dotnet test --solution ManagedCode.MCPGateway.slnx -c Release --no-build
dotnet build ManagedCode.MCPGateway.slnx -c Release --no-restore -p:RunAnalyzers=true
dotnet format ManagedCode.MCPGateway.slnx --verify-no-changes
dotnet package list --vulnerable --include-transitive
dotnet run -c Release --project benchmarks/ManagedCode.MCPGateway.Benchmarks/ManagedCode.MCPGateway.Benchmarks.csproj -- --filter "*"
```

Report commands actually run, failures, coverage limitations, benchmark environment, and actual results. A specification or proposed test name does not constitute implementation or verification.

## 14. Sources inspected

All repository references below were read from public `main`; they are mutable. Use a pinned commit when implementing.

- [S1: Uber, Designing MCP Gateway, October 1, 2026](https://www.uber.com/gb/en/blog/designing-mcp-gateway/)
- [S2: Architecture overview](https://raw.githubusercontent.com/managedcode/MCPGateway/main/docs/Architecture/Overview.md)
- [S3: README, server export and request-specific binding](https://raw.githubusercontent.com/managedcode/MCPGateway/main/README.md)
- [S4: McpGatewayToolSet](https://raw.githubusercontent.com/managedcode/MCPGateway/main/src/ManagedCode.MCPGateway/Discovery/McpGatewayToolSet.cs)
- [S5: McpGatewayToolDescriptor](https://raw.githubusercontent.com/managedcode/MCPGateway/main/src/ManagedCode.MCPGateway/Catalog/Models/McpGatewayToolDescriptor.cs)
- [S6: Runtime invocation implementation](https://raw.githubusercontent.com/managedcode/MCPGateway/main/src/ManagedCode.MCPGateway/Invocation/Internal/McpGatewayRuntime.Invocation.cs)
- [S7: Discovered tool naming and description](https://raw.githubusercontent.com/managedcode/MCPGateway/main/src/ManagedCode.MCPGateway/Discovery/Internal/McpGatewayDiscoveredToolNaming.cs)
- [S8: McpGatewaySearchMatch](https://raw.githubusercontent.com/managedcode/MCPGateway/main/src/ManagedCode.MCPGateway/Search/Models/McpGatewaySearchMatch.cs)
- [S9: McpGatewayInvokeResult](https://raw.githubusercontent.com/managedcode/MCPGateway/main/src/ManagedCode.MCPGateway/Invocation/Models/McpGatewayInvokeResult.cs)
- [S10: McpGatewayRegistry](https://raw.githubusercontent.com/managedcode/MCPGateway/main/src/ManagedCode.MCPGateway/Catalog/Internal/Registration/McpGatewayRegistry.cs)
- [S11: McpGatewayTelemetry](https://raw.githubusercontent.com/managedcode/MCPGateway/main/src/ManagedCode.MCPGateway/Gateway/Internal/Telemetry/McpGatewayTelemetry.cs)
- [S12: Performance benchmarks and documented optimization target](https://raw.githubusercontent.com/managedcode/MCPGateway/main/docs/Performance/Benchmarks.md)
- [S13: Repository development rules](https://raw.githubusercontent.com/managedcode/MCPGateway/main/AGENTS.md)
- [S14: Safe runtime diagnostics](https://raw.githubusercontent.com/managedcode/MCPGateway/main/docs/Features/SafeRuntimeDiagnostics.md)
- [S15: Official MCP draft security guidance](https://modelcontextprotocol.io/docs/draft/tutorials/security/security_best_practices)
- [S16: Official MCP draft authorization specification](https://modelcontextprotocol.io/specification/draft/basic/authorization)
- [S17: ADR-0014, SDK protocol and extension integration](https://raw.githubusercontent.com/managedcode/MCPGateway/main/docs/ADR/ADR-0014-mcp-sdk-2-protocol-and-extension-adoption.md)
- [S18: Package properties](https://raw.githubusercontent.com/managedcode/MCPGateway/main/Directory.Build.props)
- [S19: Existing invocation privacy regression tests](https://raw.githubusercontent.com/managedcode/MCPGateway/main/tests/ManagedCode.MCPGateway.Tests/Invocation/McpGatewayInvocationPrivacyTests.cs)
- [S20: McpGatewayInvokeRequest](https://raw.githubusercontent.com/managedcode/MCPGateway/main/src/ManagedCode.MCPGateway/Invocation/Models/McpGatewayInvokeRequest.cs)
- [S21: McpGatewaySearchRequest](https://raw.githubusercontent.com/managedcode/MCPGateway/main/src/ManagedCode.MCPGateway/Search/Models/McpGatewaySearchRequest.cs)


## 15. Current implementation decisions and tracking

Profiling follow-up (2026-10-12): the user renewed profiling scope for both MarkdownLd.Kb and MCPGateway. Verify the installed tool and existing skill; run the real 120-tool gateway benchmark catalog through a bounded `--profile` mode; retain allocation/GC/counter captures and separate uninstrumented per-operation metrics. Use the existing owner captures/full benchmarks for before/after comparison and collect current owner evidence where it resolves a remaining hotspot. Map stacks to source before changing algorithms. Finish published owner adoption, real consumer regressions, coverage, strict formatting/analyzers, vulnerability scans, and the full BenchmarkDotNet suite. CPU sampling remains unsupported on macOS in diagnostics 0.28.0. Owner 0.2.13 release succeeded and its exact public NuGet package was installed and verified against commit aa05335983926a7d0ddde67b1d0608bb159e8e22.

The user authorized all A-F, dependency updates, and owned MarkdownLd.Kb repairs, then explicitly deferred that implementation after urgently publishing the CLI. Current active scope is the subsequently requested diagnostics skill, MarkdownLd.Kb graph/ontology/query repairs, explicit optional full-text/federated flows, canonical owner release, and gateway consumer integration. Optional OpenAPI/gRPC remain future work. Preserve the primary checkouts and unrelated edits.

Assessment: progressive discovery, schema-faithful proxies, result projection, and governed publication fit the library. Uber-specific mesh, crawler service, UI, identity providers, billing, and databases stay host-owned. Tool annotations do not confer authorization, idempotency, or safety. Registered local tools retain the unmanaged default; managed mode requires real policy/publication services and trusted host context.

Execution order and completion criteria:

- [x] Pin baseline, read source plan/article and root/local guidance, establish full TUnit baseline.
- [x] Update all stable direct NuGet references, investigate transitive advisories, verify compatibility; final consumer uses MarkdownLd.Kb 0.2.15, Communication 10.3.7 and TUnit 1.74.0. Final audits report no outdated direct or vulnerable direct/transitive packages.
- [x] Repair schema candidate filtering and focused projection in MarkdownLd.Kb with real SPARQL regression flows, coverage and canonical patch releases; 0.2.15 public artifact/repository SHA verified before consumer restore.
- [ ] A: trusted immutable host context, policy/publication enforcement across tools/search/schema/graph/prompts/resources/tasks; identity-isolation and bypass tests.
- [ ] B: definition/schema fingerprints, CAS publication/diffs, atomic validated runtime switch, rollback preserving revocations, source-scoped refresh and leased in-flight bindings; concurrent/failure tests.
- [ ] C: bounded compact discovery, schema lookup, schema-faithful proxies, opt-in discovery-only official-SDK export; real client/server tests and payload limits.
- [ ] D: serializable outcomes, projection and byte limits, result policy applied to every outward representation, authenticated host-owned retained results; JSON/content/privacy/expiry tests.
- [ ] E: identity-specific upstream bindings, explicit outbound policy, deadlines/concurrency/bounded queue/circuit/quota with no blind mutation retries; isolation/cancellation/source-failure tests.
- [ ] F: bounded invocation/policy/refresh/output telemetry, measured bytes and allocations, current full benchmark suite and docs.
- [x] CLI initial publication: `mcpgateway` 0.1.0 includes official-SDK search/schema/call/output; project is in the solution and has a separate pack/push workflow. Commit `dafc0a69114fb63c41410e3242f14643f4f7919f`; [publication run](https://github.com/managedcode/MCPGateway/actions/runs/38178560321) succeeded. User explicitly requested no local tests or install verification for name reservation; that qualification remains future work.
- [x] Self-review, format, strict build/analyze, focused and full TUnit, coverage, cloc, Roslynator, all-dependency vulnerability audit, full BenchmarkDotNet results and final documentation. Final gateway: 351/351 tests, 92.24% line coverage, zero build warnings/errors and Roslynator diagnostics; 12/12 standard-job benchmarks in 1079.14s; no outdated direct or vulnerable direct/transitive packages.

Test methodology: reproduce defects with real package flows; official SDK client/server for protocol behavior; deterministic embeddings only in explicit vector tests. Test success/denial, revocation, forged context, concurrent identities/publication/invocation, cancellation, partial source failure, schema drift, projection escaping/arrays, alternate content leaks, retained-result ownership/expiry, outbound denial and queue limits. Preserve every baseline test. Record actual commands and terminal evidence here as work progresses.

Baseline failing tests: none (342 passed). Remaining risks: broad released API surface, in-flight upstream ownership, dynamic authorization cache partitions, task/source binding isolation, output-schema validity, NuGet package-ID reservation/publication and long full benchmarks.

Current checkpoint: broad A-F implementation is deferred. The incomplete discovery draft remains in [DeferredDiscoveryDraft.patch](DeferredDiscoveryDraft.patch). CLI `mcpgateway` 0.1.0 is published; further CLI work remains low priority. The active follow-up delivers diagnostics, graph/ontology/query correctness and measured allocations in the two owning packages.

`MarkdownLd.Kb` 0.2.14 is published from scoped commit `3629a8665efed78324e30b36ce629edeed3e4352`; [canonical release](https://github.com/managedcode/markdown-ld-kb/actions/runs/38189099311) succeeded, including all seven full benchmark jobs. The exact public flat-container artifact returned HTTP 200 at 00:55 UTC on October 12, 2026; restored nuspec confirms that version and commit. Owner local qualification: 411/411 TUnit cases, 97.223% module coverage (schema entrypoint 97.03%, changed compiler helpers 100%), all 131 benchmarks in 405.91 seconds, warning-free strict build, clean format/size/package audits. Unrelated owner working-tree changes remain visible and uncommitted.

Delivered owner behavior: authoritative candidate VALUES on the full graph; bounded provenance OPTIONAL joins; correct RDF fragment identity and SHACL targeting; ontology declarations/snapshots/profiles; separate related and next-step roles with complete RDF types. Measured compact/indexed query generation reduces rich-catalog candidate matching 73.83 -> 9.41 MiB/op and ShortDocuments 7.68 -> 5.32 MiB/op. Official split packages retain graph/ontology/full-text/federation/SPIN without HTML/DataTables. Existing Lucene search and explicit allowlisted federation are tested; no new SPIN execution API is claimed.

Diagnostics: installed global `dotnet-diagnostics-mcp` 0.28.0, validated the reusable profiling skill and official-SDK JSONL helper. Real owner and 120-tool gateway workloads retain allocation/GC/counter captures. CPU sampling is unavailable on macOS in this version. A standalone dotNetRDF 3.5.2 reproduction found UsePLinqEvaluation=false while intermediate result multisets still enable PLINQ; reported as [upstream issue 910](https://github.com/dotnetrdf/dotnetrdf/issues/910). Keep the official engine, no fork or consumer workaround.

The consumer now binds candidate subjects on its existing graph rather than rebuilding a lossy snapshot. Nine new generated-Markdown/real-SDK cases pass on published 0.2.14: primary pool selection/empty/unknown/invalid inputs, cross-tool evidence and both expansion roles, real MCP invocation, and Hybrid pool preservation through ranking/fallback. Full consumer test run passes 351/351; initial final module coverage is 92.21%, changed graph files 88.57–94.87%. Stable package audit has no outdated direct references; transitive vulnerability audit has none. Reconfirm final checks after the final TUnit update and runtime/benchmark measurements.

Performance evidence distinguishes the old lossy candidate-copy baseline from correct full-graph retrieval. Initial integration on published 0.2.13 allocated about 293 decimal MB/op weather and 304 MB/op archive. Real 56,168-triple export and official-engine BGP observations attributed this to fully-variable triple scans. Controlled compiler experiments preserve all 32 raw rows and reduce raw query allocations 237.12 -> 8.68 decimal MB/op. Rejected query shapes and small-graph regressions are documented in the owner report. Final gateway follow-up and all 12 full BenchmarkDotNet cases remain the consumer release gate; owner figures alone do not establish gateway speedups.

The published 0.2.14 live follow-up reduced weather 292.917 -> 23.815 decimal MB/op and archive 304.206 -> 35.403, but showed a second owned hotspot: full focused graph enumeration owns 59.25% of allocation samples. Scope continues in 0.2.15 with official subject/object indexes and real focused graph/merge/non-URI tests, full owner qualification/publication and then final consumer adoption. Detailed receipts and comparison boundaries are in GatewaySearchProfiling.md.

Focused projection owner qualification completed: eight new real flow cases; local 419/419 tests; module 97.2295%, changed projection/expansion 100%; all 131 BDN cases in 434.31 seconds. Rich candidate 9.41 -> 5.95 MiB, simple candidate 5.32 -> 4.78, no-match broadly unchanged. Scoped owner commit `764a9fbc1a7cc0f47d228a4fe5a777bf1de4d876` contains only the focused projection/benchmark/tests/docs, TUnit 1.74.0 and canonical 0.2.15 version. Preserved serialization/tokenization changes remain uncommitted. Publication/feed verification and final consumer adoption follow.

Owner 0.2.15 release 38191832238 completed successfully. Committed-source CI: 411/411 tests and all 131 unique benchmark cases validated from downloaded JSON. Exact public nupkg returned 200 at 01:34:54 UTC; annotated v0.2.15 resolves to 764a9fbc1a7cc0f47d228a4fe5a777bf1de4d876. The consumer restored that public version and verified its repository SHA. Final strict build/analyze has zero warnings/errors; all 351 TUnit cases pass; line coverage is 92.24%, with changed graph files at 88.57–94.87%. Format and Roslynator are clean. All 12 full gateway benchmarks are qualified in 1079.14s; README and performance docs contain the current snapshot. Canonical consumer release/feed verification is required before declaring package publication.

Final live allocation attribution: focused schema projection drops from 59.25% to 0.0657% sample frequency on published 0.2.15. Weather counters drop 23.815 → 9.726 decimal MB/op and archive 35.403 → 21.292. Remaining no-match token-distance fallback costs about 39.2 decimal MB/op; its complete `ToSemanticSnapshot()` owns 90.6313% of samples. The next experiment belongs in MarkdownLd.Kb: rank primary token-distance IDs first, retrieve the required semantic neighborhood through official RDF indexes, and prove identical labels, groups, related/next-step roles, scores, non-URI identities and post-merge behavior. Keep strict schema no-match separate. See [GatewaySearchProfiling](../Performance/GatewaySearchProfiling.md) for captures and full qualification boundaries.


Coverage delivery hardening (0.4.20): the 0.4.19 release/CI/CodeQL succeeded and all 12 cases were validated in both CI and release, but terminal CI coverage logs disclosed zero instrumented modules. Local coverage remains valid. Reproducing `ContinuousIntegrationBuild=true` in the same checkout confirms SourceLink PDB documents use `/_/` and the CLI needs explicit source-root mapping. The unchanged CI-built assembly fails the 85% line gate without mapping (exit 2, zero modules), then passes with the official mapping file (351/351 tests, 92.22% line). CI and release now use this mapping, scope coverage to the production library, and enforce 85% before benchmark/pack/push. Runtime code and measured performance behavior are unchanged. Public 0.4.19 version/commit and all payload entries are verified against the canonical release; NuGet adds its repository signature. 0.4.20 release must provide nonempty coverage artifacts and exact public-package verification.
