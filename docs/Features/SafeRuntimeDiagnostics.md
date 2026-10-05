# Safe Runtime Diagnostics

## Purpose

Runtime failures from tools, graph providers, embedding services, source registrations, and optional normalizers can contain caller data, credentials, or provider internals. The gateway exposes diagnostics and logs to hosts, so failure reporting must use bounded package-owned categories rather than exception payloads.

## Requirements

Fatal exception classification reuses `ManagedCode.Communication.CQRS.CqrsRuntimeFailures` from the centrally pinned `ManagedCode.Communication` package; MCPGateway does not duplicate the native CQRS fatal taxonomy.

### REQ-DIAG-001: Safe failure surfaces

Every recoverable runtime failure surfaced as an `McpGatewayDiagnostic`, `McpGatewayInvokeResult.Error`, or gateway log MUST use a fixed package-owned category and message. It MUST NOT include exception objects, exception messages, inner-exception messages, caller queries or arguments, source endpoints, credentials, or other provider-controlled text.
Fatal runtime exceptions, including fatal exceptions nested in an `AggregateException`, MUST propagate instead of becoming normal invoke/search/index results.

### REQ-DIAG-002: Preserve operation semantics

Replacing failure details MUST preserve each operation's existing success, fallback, empty-result, and failure behavior. Caller cancellation MUST continue to propagate as cancellation and MUST NOT be converted to a diagnostic-only failure.

### REQ-DIAG-003: Bounded observable metadata

Failure logs MUST retain only fixed event/category identifiers and other explicitly package-owned non-sensitive dimensions. They MUST NOT log tool/source identifiers or raw exception state on recoverable provider failures.

## Acceptance Criteria

### AC-DIAG-001: Invocation error privacy

Given a real local `AIFunction` that throws an exception containing unique secret markers, invocation returns the fixed invocation-failure diagnostic; captured log state and exception fields and the public result contain none of those markers, the supplied query, or supplied argument values.

### AC-DIAG-002: Graph search error privacy

Given a reachable graph-search failure containing unique query/provider markers, the gateway preserves its current empty/fallback behavior and emits only the fixed graph-search diagnostic. Captured logs and returned diagnostics contain none of the markers, query text, endpoint text, or exception text.

### AC-DIAG-003: Other recoverable provider failures

Failure handling for normalization, vector search, graph build, source load, embedding generation, and embedding-store load/save uses the same fixed-category rule. Focused tests cover representative provider failures and source inspection verifies the remaining catch sites.

### AC-DIAG-004: Cancellation and fatal failures

When an operation's caller token is cancelled, the original `OperationCanceledException` behavior remains observable; the gateway does not return a sanitized provider failure in its place. Direct and aggregate-wrapped fatal runtime exceptions also escape invocation rather than becoming `IsSuccess = false`.

## Implementation Ownership

- `src/ManagedCode.MCPGateway/Gateway/Internal/Runtime/`: package-owned diagnostic categories and message templates.
- `src/ManagedCode.MCPGateway/Invocation/`, `Search/`, `Catalog/`, and `Hosting/`: recoverable failure boundaries.
- `tests/ManagedCode.MCPGateway.Tests/Invocation/` and `Search/Graph/`: real `AIFunction`, reachable graph, captured logger, and cancellation regressions.

## Verification

Test mapping:

- AC-DIAG-001 and AC-DIAG-004: `tests/ManagedCode.MCPGateway.Tests/Invocation/McpGatewayInvocationPrivacyTests.cs`
- AC-DIAG-002: `tests/ManagedCode.MCPGateway.Tests/Search/Graph/McpGatewayGraphFailurePrivacyTests.cs`
- AC-DIAG-003: recoverable catch-site audit across source loading, graph indexing/search, vector search, normalization, embedding persistence/generation, and warmup.

- `dotnet build ManagedCode.MCPGateway.slnx -c Release --no-restore`
- `dotnet build ManagedCode.MCPGateway.slnx -c Release --no-restore -p:RunAnalyzers=true`
- `dotnet test --solution ManagedCode.MCPGateway.slnx -c Release --no-build`
- `dotnet format ManagedCode.MCPGateway.slnx --verify-no-changes --no-restore`
- Roslynator analysis and package vulnerability audit as required by repository policy.

## Related Decision

- [ADR-0016: Safe Runtime Diagnostics](../ADR/ADR-0016-safe-runtime-diagnostics.md)
