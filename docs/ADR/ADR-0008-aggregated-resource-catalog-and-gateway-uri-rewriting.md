# ADR-0008: Aggregated Resource Catalog And Gateway URI Rewriting

## Context

`ManagedCode.MCPGateway` already aggregated tools and prompts across multiple upstream MCP servers, but it did not expose MCP resources even though the official `modelcontextprotocol/csharp-sdk` already supports:

- direct resources
- resource templates
- `resources/read`

This repository now treats the official C# SDK as the MCP baseline rather than as an optional subset reference. That means resource support cannot be left out of the package surface when the underlying SDK already supports it.

Aggregating resources also introduces a collision problem that tools and prompts do not share in the same form. Upstream MCP servers may legitimately reuse the same URI schemes and resource paths. If the gateway simply forwarded upstream resource URIs unchanged through `WithMcpGatewayCatalog()`, downstream `resources/read` calls would be ambiguous because the exported gateway server would no longer know which upstream `SourceId` owned a duplicated URI.

## Decision

`ManagedCode.MCPGateway` will add an `IMcpGatewayResourceCatalog` public surface for:

- listing direct resources across registered MCP sources
- listing resource templates across registered MCP sources
- reading a concrete resource URI through an explicit `SourceId`

`WithMcpGatewayCatalog()` will also export aggregated resources through the official MCP resource handlers. During downstream export, the gateway will rewrite upstream resource URIs and URI templates into gateway-owned opaque URIs that embed the upstream `SourceId` reversibly. Public package consumers using `IMcpGatewayResourceCatalog` will still see the original upstream URIs; only the downstream exported MCP server uses the rewritten URIs.

## Consequences

Positive:

- the package now covers the MCP resource surface already available in the official C# SDK
- hosts can inspect and read aggregated resources without bypassing the gateway package
- downstream MCP resource reads remain unambiguous even when multiple upstream servers reuse the same URI spaces
- the public package API stays source-aware instead of hiding collisions behind brittle global URI assumptions

Trade-offs:

- the package now owns another catalog surface that must stay synchronized with docs and tests
- exported MCP resource URIs are intentionally opaque and gateway-owned rather than preserving the upstream URI verbatim
- public `IMcpGatewayResourceCatalog` callers must provide the concrete expanded resource URI for templated resources

## Invariants

- `IMcpGatewayResourceCatalog` MUST list direct resources and resource templates separately.
- `IMcpGatewayResourceCatalog.ReadResourceAsync(...)` MUST require `SourceId` plus a concrete resource URI.
- `WithMcpGatewayCatalog()` MUST export resources through the official MCP `resources/list`, `resources/templates/list`, and `resources/read` handlers.
- Exported downstream resource URIs MUST be reversible back to the upstream `SourceId` and original URI so reads remain deterministic.
- The package MUST keep the original upstream URIs in the public resource catalog surface rather than leaking the gateway-owned rewritten URIs into host application code.

## Verification

- `dotnet restore ManagedCode.MCPGateway.slnx`
- `dotnet build ManagedCode.MCPGateway.slnx -c Release --no-restore`
- `dotnet build ManagedCode.MCPGateway.slnx -c Release --no-restore -p:RunAnalyzers=true`
- `dotnet test --solution ManagedCode.MCPGateway.slnx -c Release --no-build`
- `roslynator analyze src/ManagedCode.MCPGateway/ManagedCode.MCPGateway.csproj -p Configuration=Release --severity-level warning`
- `roslynator analyze tests/ManagedCode.MCPGateway.Tests/ManagedCode.MCPGateway.Tests.csproj -p Configuration=Release --severity-level warning`
- `cloc --include-lang=C# src tests`
