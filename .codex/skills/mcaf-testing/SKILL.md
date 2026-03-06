---
name: mcaf-testing
description: "Add or update automated tests for a bugfix, feature, or refactor using this repository's TUnit-based rules from `AGENTS.md`. Use TDD where possible, prefer integration-style gateway tests, and verify local tools, MCP tools, vector search, and lexical fallback with real assertions."
compatibility: "Requires the repository build and test tooling; uses commands from AGENTS.md."
---

# MCAF: Testing

## Outputs

- new or updated automated tests that encode documented behavior
- for new behavior and bugfixes: tests drive the change when practical
- updated verification sections in docs when those docs exist and the test plan changed
- evidence of verification: commands run, result, and artifact path when applicable

For this repository:

- test framework is TUnit
- package behavior tests live in `tests/ManagedCode.MCPGateway.Tests/`
- repository-wide execution uses `dotnet test --solution ManagedCode.MCPGateway.slnx ...`
- xUnit is not allowed here

## Workflow

1. Read `AGENTS.md`:
   - canonical commands: `build`, `analyze`, `test`, `format`, `pack`
   - testing rules and verification order
2. Start from the docs that define behavior when they exist:
   - `docs/Features/*` for user and system flows
   - `docs/ADR/*` for architectural invariants
   - if docs do not exist yet, derive scenarios from the task, `README.md`, public API, and current tests
3. Follow `AGENTS.md` verification timing:
   - use the smallest scope that proves the change first
   - expand to the required suite after the focused tests pass
4. Define the scenarios you must prove:
   - positive
   - negative
   - edge
   - for ADRs, invariants and must-not-happen behaviors
5. Choose the highest meaningful test level:
   - prefer integration-style package tests when behavior crosses boundaries
   - use smaller isolated tests only when higher-level coverage is impractical
6. Implement via a TDD loop when possible:
   - write the test first
   - confirm it fails for the right reason
   - implement the smallest change to make it pass
   - refactor while keeping tests green
7. Use repository-specific TUnit conventions:
   - prefer `TUnit.Core.Test` for attributes
   - use `Assert.That(...)` assertions
   - run solution tests with `dotnet test --solution ...`
   - never add `xunit` packages or assertions
8. Write tests that assert outcomes, not just execution:
   - returned values and search results
   - normalized tool outputs
   - emitted side effects when they are observable
9. Keep tests stable:
   - deterministic fixtures
   - no hidden network dependencies unless the test is explicitly about transport
   - avoid sleep-based timing
10. For `ManagedCode.MCPGateway`, keep coverage focused on real gateway behavior:
   - local tool indexing and invocation
   - MCP tool indexing and invocation
   - vector search behavior
   - lexical fallback behavior
11. Run verification in layers:
   - focused or changed tests first when practical
   - `build`
   - `analyze` if the change touched code paths that could trip analyzers
   - `test`
   - `pack` when package output is affected
12. Keep docs and skills consistent:
   - if you changed test commands or rules, update `AGENTS.md` and relevant skills in the same change

## Guardrails

- All test discipline and prohibitions come from `AGENTS.md`. This skill must not contradict it.
- Do not remove tests to get a green build.
- Do not downgrade behavior coverage from integration-style tests without a concrete reason.
