# MCP task durability acceptance

Direction: [brainstorm](mcp-task-durability.brainstorm.md). Durable contract: README Tasks section, docs/Architecture/Overview.md and a task-store ADR.

- A task and its trusted subject/tenant/authorization partition/policy revision/source definition binding are created atomically in the selected host store before its handle is returned. Fresh gateway instances must read that binding from storage; a forged/foreign caller remains denied.
- A long active or input-required task in the bounded default store does not disappear after result-retention time. A terminal task/result is retained for the configured duration and then expires; capacity admission stays bounded and terminal state is immutable. Verify this through real official SDK client/server journeys with short deterministic retention windows.
- A recreated SDK server/DI container with a recreated durable store can retrieve a prior completed result, access current input state and cancel existing work, with current source publication/access checks still enforced. Persist fixture storage between fresh host instances; do not keep the old wrapper's memory alive as evidence.
- Prostir persists the same metadata in its existing task owner. Its Worker/Flow tasks can be observed to the real terminal business effect after reconnect/reactivation and the requested Gateway restart scenario. No browser cancellation controls accepted work; explicit cancellation uses existing owner commands.
- Ship all confirmed owner defects together in one patch release, verify GitHub publication and actual NuGet availability, update Prostir once to that version and run consumer regressions plus the canonical full build. Remove the temporary 30-minute/process-local limitation note after this evidence passes.

Current status: owner implementation and all 451 tests passed, including five new real SDK transport cases. The full coverage run passed at 92.46% line coverage against the 85% gate. Publication and Prostir process-restart acceptance remain pending. Tests assert terminal caller-visible outcomes and forbidden-effect isolation.
