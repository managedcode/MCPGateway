# ADR-0009: MCP Export Completion, Subscriptions, And Logging Parity

## Context

After `ADR-0008`, `ManagedCode.MCPGateway` exported aggregated MCP tools, prompts, and resources, but the downstream MCP server export still lagged behind the stable protocol surface already available in the official `modelcontextprotocol/csharp-sdk`:

- `completion/complete`
- `resources/subscribe`
- `resources/unsubscribe`
- forwarded `notifications/resources/updated`
- `logging/setLevel`

The repository now treats the official C# SDK as the MCP baseline. Stable MCP export features that the SDK already supports cannot remain absent from the gateway layer without creating a protocol subset that surprises downstream clients.

## Decision

`WithMcpGatewayCatalog()` will proxy stable MCP completion and subscription flows in addition to list/read flows:

- completion requests for exported prompts and resource references will be resolved back to the owning upstream `SourceId` and proxied through the upstream MCP client
- resource subscriptions will be created against the owning upstream MCP client and tracked per downstream MCP session plus exported gateway URI
- upstream `notifications/resources/updated` messages received through those subscriptions will be forwarded to the subscribed downstream client, but with the exported gateway URI instead of the raw upstream URI
- the gateway export will advertise MCP logging capability and accept `logging/setLevel`, relying on the SDK server runtime to track the latest requested logging level on the downstream server instance

No new public catalog surface is introduced for these protocol features. They remain part of the downstream MCP export behavior owned by the `Hosting` slice.

## Consequences

Positive:

- downstream MCP clients can now use prompt and resource completions against the aggregated gateway export
- resource subscriptions become source-aware and continue to work when resource URIs are gateway-rewritten
- forwarded resource update notifications keep downstream clients on gateway-owned URIs instead of leaking upstream transport details
- the exported MCP server behavior is closer to the stable SDK baseline without widening the public DI surface unnecessarily

Trade-offs:

- hosting now owns per-session subscription bookkeeping and notification forwarding
- completion and subscription proxying depend on source resolution at request time, which adds catalog lookups on those protocol paths
- the package still does not take on experimental MCP task APIs as part of the default gateway surface

## Invariants

- `WithMcpGatewayCatalog()` MUST export MCP `completion/complete`.
- `WithMcpGatewayCatalog()` MUST export MCP `resources/subscribe` and `resources/unsubscribe`.
- Forwarded `notifications/resources/updated` MUST use the exported gateway URI that the downstream client subscribed to, not the raw upstream URI.
- Completion requests for exported prompts and resources MUST resolve back to the owning upstream `SourceId` before proxying.
- `logging/setLevel` support MUST stay aligned with the official SDK server behavior instead of introducing a gateway-specific logging protocol.

## Verification

- `dotnet restore ManagedCode.MCPGateway.slnx`
- `dotnet build ManagedCode.MCPGateway.slnx -c Release --no-restore`
- `dotnet build ManagedCode.MCPGateway.slnx -c Release --no-restore -p:RunAnalyzers=true`
- `dotnet test --solution ManagedCode.MCPGateway.slnx -c Release --no-build`
- `roslynator analyze src/ManagedCode.MCPGateway/ManagedCode.MCPGateway.csproj -p Configuration=Release --severity-level warning`
- `roslynator analyze tests/ManagedCode.MCPGateway.Tests/ManagedCode.MCPGateway.Tests.csproj -p Configuration=Release --severity-level warning`
- `cloc --include-lang=C# src tests`
