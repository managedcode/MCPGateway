# ADR-0016: Safe Runtime Diagnostics

Status: Accepted<br>
Date: 2026-10-05

## Context

Recoverable provider and tool failures are currently converted into gateway diagnostics and logs. Provider exceptions can embed queries, tool arguments, endpoint credentials, or remote response details. Returning exception messages to callers or passing exception objects to logging providers makes those values observable beyond the failing provider.

## Decision

The gateway reports recoverable runtime failures through closed, package-owned diagnostic categories with fixed messages. Exception instances and their messages are not included in logs, public results, or diagnostics. Provider identifiers and request-derived text are also excluded from failure log state. Existing operation behavior remains unchanged: invocation returns its failure result, optional search providers retain their existing fallback/empty behavior, and caller cancellation propagates. Fatal runtime exceptions, including fatal exceptions nested in an aggregate, propagate rather than becoming ordinary failure results.

Fatal classification delegates to the published `ManagedCode.Communication` package's `CqrsRuntimeFailures.FindFatal`; its version is centrally pinned. MCPGateway does not clone the Communication exception taxonomy or add an application-side fatal-exception list.

The implementation covers local and remote invocation, graph search and graph build, vector search and embedding generation/store access, query normalization, catalog source loading, and hosted index warmup. The policy applies at each recoverable failure boundary; it does not suppress fatal process failures or change exception propagation outside those boundaries.

## Requirements and Acceptance

- Implements REQ-DIAG-001 through REQ-DIAG-003 in `docs/Features/SafeRuntimeDiagnostics.md`.
- Automated acceptance is AC-DIAG-001 through AC-DIAG-004 in that feature specification.

## Implementation Contract

1. Define stable package-owned error categories and fixed safe messages in the runtime's feature-local diagnostics ownership.
2. Replace raw exception logging, exception-message diagnostics, and exception-derived invocation results at recoverable runtime boundaries without changing their fallback or cancellation semantics.
3. Add TUnit canaries with real local `AIFunction` and reachable graph provider failures, capturing both public results and logger state; cover cancellation and direct/aggregate-wrapped fatal exceptions.
4. Audit every reachable runtime catch/log/diagnostic path for raw exception or request/provider text.
5. Update the feature specification, architecture overview, and package release notes before release.

## Verification

Run the owning repository's Release restore/build, analyzer build, complete TUnit suite, format verification, Roslynator analysis, package audit, and release workflow. Canary assertions must inspect formatted log state and attached exception fields, not only the rendered message.

## Consequences

- Hosts receive stable actionable categories without provider-controlled data.
- Debugging provider internals requires provider-owned secure telemetry; MCPGateway will not duplicate raw diagnostics.
- Existing fallback and cancellation behavior remains stable.

## Rollout and Rollback

Ship as an additive patch release because the change narrows diagnostic detail without changing public type shape. If a category proves insufficient, add a new fixed safe category or package-owned code; do not restore exception text to public diagnostics or logs.
