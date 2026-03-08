# AGENTS.md

Project: ManagedCode.MCPGateway
Owned by: ManagedCode.MCPGateway package maintainers

Parent: `../../AGENTS.md`

## Purpose

- This project ships the reusable `ManagedCode.MCPGateway` NuGet library.
- It owns the public gateway facade, DI registration, tool catalog/runtime orchestration, and optional embedding and warmup integrations.

## Entry Points

- `McpGateway.cs`
- `McpGatewayToolSet.cs`
- `McpGatewayAutoDiscoveryChatClient.cs`
- `Registration/`
- `Internal/Runtime/`
- `Internal/Catalog/`

## Boundaries

- In scope: public package APIs, internal runtime orchestration, catalog mutation infrastructure, transport registration, serialization, and embedding-store integration.
- Out of scope: app-specific hosts, sample-only glue, test-only helpers, and CI workflow authoring.
- Protected or high-risk areas: public API shape, search ranking behavior, MCP transport integration, runtime concurrency, and tool invocation contracts.

## Project Commands

- `build`: `dotnet build src/ManagedCode.MCPGateway/ManagedCode.MCPGateway.csproj -c Release --no-restore`
- `test`: `dotnet test --solution ManagedCode.MCPGateway.slnx -c Release --no-build`
- `format`: `dotnet format ManagedCode.MCPGateway.slnx`
- `analyze`: `dotnet build src/ManagedCode.MCPGateway/ManagedCode.MCPGateway.csproj -c Release --no-restore -p:RunAnalyzers=true`

For this .NET project:

- test framework: `TUnit` in the solution test project
- runner model: `Microsoft.Testing.Platform`
- analyzer severity lives in the repo-root `.editorconfig`

## Applicable Skills

- `mcaf-dotnet`
- `mcaf-dotnet-analyzer-config`
- `mcaf-dotnet-code-analysis`
- `mcaf-dotnet-codeql`
- `mcaf-dotnet-format`
- `mcaf-dotnet-features`
- `mcaf-dotnet-roslynator`
- `mcaf-testing`
- `mcaf-dotnet-tunit`
- `mcaf-dotnet-quality-ci`
- `mcaf-dotnet-complexity`
- `mcaf-solid-maintainability`
- `mcaf-architecture-overview`
- `mcaf-adr-writing`
- `mcaf-feature-spec`

## Local Constraints

- Stricter maintainability limits: none beyond root defaults unless a subfolder documents them explicitly.
- Required local docs: keep `README.md`, `docs/Architecture/Overview.md`, and matching ADRs in sync when public behavior or architecture changes.
- Local exception policy: document any temporary size or complexity breach in the nearest ADR, feature doc, or follow-up note before leaving the task.

## Local Rules

- Keep the project package-first and reusable; do not add app-specific hosting assumptions.
- Keep `McpGateway` focused on search and invoke orchestration; registry mutation belongs in catalog and registration collaborators.
- Keep transport-specific logic inside registration and source abstractions, not scattered through runtime code.
- When production behavior changes here, update the integration-style tests under `tests/ManagedCode.MCPGateway.Tests/` in the same task.
