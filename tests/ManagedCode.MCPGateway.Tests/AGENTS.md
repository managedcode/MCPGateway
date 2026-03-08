# AGENTS.md

Project: ManagedCode.MCPGateway.Tests
Owned by: ManagedCode.MCPGateway package maintainers

Parent: `../../AGENTS.md`

## Purpose

- This project verifies the shipped behavior of `ManagedCode.MCPGateway` through integration-style tests.
- It owns regression coverage for local tools, MCP tools, search, invocation, embeddings, meta-tools, and chat-client integration.

## Entry Points

- `Search/`
- `Invocation/`
- `MetaTools/`
- `ChatClient/`
- `Agents/`
- `TestSupport/`

## Boundaries

- In scope: caller-visible gateway behavior, deterministic test doubles, MCP test hosts, and integration-style assertions.
- Out of scope: production package implementation, packaging metadata, and consumer-facing README examples.
- Protected or high-risk areas: parallel test isolation, deterministic embedding behavior, MCP transport test hosts, and regression coverage for search ranking and invocation.

## Project Commands

- `build`: `dotnet build tests/ManagedCode.MCPGateway.Tests/ManagedCode.MCPGateway.Tests.csproj -c Release --no-restore`
- `test`: `dotnet test --solution ManagedCode.MCPGateway.slnx -c Release --no-build`
- `format`: `dotnet format ManagedCode.MCPGateway.slnx`
- `analyze`: `dotnet build tests/ManagedCode.MCPGateway.Tests/ManagedCode.MCPGateway.Tests.csproj -c Release --no-restore -p:RunAnalyzers=true`

For this .NET project:

- test framework: `TUnit`
- runner model: `Microsoft.Testing.Platform`
- analyzer severity lives in the repo-root `.editorconfig`

## Applicable Skills

- `mcaf-dotnet-analyzer-config`
- `mcaf-dotnet-code-analysis`
- `mcaf-dotnet-format`
- `mcaf-testing`
- `mcaf-dotnet-roslynator`
- `mcaf-dotnet-tunit`
- `mcaf-dotnet-quality-ci`
- `mcaf-dotnet-complexity`
- `mcaf-dotnet-features`

## Local Constraints

- Stricter maintainability limits:
  - `file_max_loc`: `400`
  - `type_max_loc`: `300`
  - `function_max_loc`: `90`
  - `max_nesting_depth`: `4`
- Required local docs: keep test names and helper structure aligned with the behavior documented in `README.md` and `docs/Features/`.
- Local exception policy: any temporary breach must be justified in this file or the nearest durable test doc before the task is closed.

## Local Rules

- Keep tests focused on observable gateway behavior, not internal implementation trivia.
- Prefer deterministic local fakes and test helpers over network-dependent or timing-fragile assertions.
- Do not weaken parallel isolation to hide shared-state bugs; fix the shared state instead.
- When a production change affects search or invocation semantics, add or update the corresponding test coverage in the matching test area.
