# MCP task durability repair plan

References: [brainstorm](mcp-task-durability.brainstorm.md), [acceptance](mcp-task-durability.acceptance.md).

- [x] Inspect the owning SDK/store/hosting sources, authorization pipeline, existing tests and release workflow. Confirm process-local owner loss and independent TTL eviction.
- [x] Add the gateway task-store contract with atomic trusted-owner creation; replace the process-local authorization registry and document the host persistence boundary.
- [x] Implement the bounded default store with terminal-result retention using the existing MemoryCache dependency and native SDK models. Preserve first terminal outcome, inputs, capacity and cancellation.
- [x] Add real SDK transport regressions for long active/input-required work, terminal retention, fresh-host/store recovery, tenant/source-policy isolation and recovered cancellation. Update affected current-contract tests and README/architecture/ADR together.
- [x] Self-review the complete batch and run tool restore, restore, Release build/analyze, full tests, formatting check, Roslynator, cloc, coverage and vulnerability audit according to AGENTS.md.
- [ ] Increment the canonical package patch version once, commit only this repair, push normally, follow Release CI through NuGet/GitHub success and verify feed availability.
- [ ] Update Prostir once to the published package; persist authorization metadata in its existing task grain, restore terminal observations through existing Worker/Flow owners, and remove the temporary limitation note.
- [ ] Run the canonical Prostir build and focused real transport/restart regressions. Keep unresolved checks explicit; no completion claim before required gates pass.

Constraints: no new runtime dependency, persistence infrastructure, grain, serializer, effect replay, local unpublished package, project-reference workaround, stash, force-push or unrelated cleanup. Existing local .config/dotnet-tools.json changes are unrelated and stay visible.

## Current verification

The 2.3.2 Release build and explicit analyzer build passed with zero warnings/errors. All 451 tests passed, including five new SDK transport cases. Roslynator reports zero diagnostics; the NuGet audit reports no vulnerable packages in any project. Formatting verification and the scoped diff check passed; the full 451-test coverage run passed with 92.46% line coverage (85% required). The canonical package version was incremented once to 2.3.2 for this complete repair batch. Publication and Prostir acceptance remain pending.
