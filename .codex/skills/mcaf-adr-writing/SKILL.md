---
name: mcaf-adr-writing
description: "Create or update an ADR under `docs/ADR/` with context, decision, alternatives, consequences, rollout, verification, and Mermaid diagrams. Use when changing architecture, dependencies, public boundaries, data flow, or cross-cutting package patterns."
compatibility: "Requires repository write access; produces Markdown docs with Mermaid diagrams."
---

# MCAF: ADR Writing

## Outputs

- `docs/ADR/ADR-XXXX-<short-title>.md` (create or update)
- update `docs/Architecture/Overview.md` when boundaries or interactions change

## Decision Quality

Before writing, make the ADR executable:

- decision: one direct sentence
- scope: what changes and what does not
- no invented reality: every component exists today or is explicitly part of this change
- invariants: write them as MUST or MUST NOT statements
- verification: use exact commands from `AGENTS.md`
- stakeholders: include what Product, Dev, QA, and DevOps must know

## Workflow

1. Confirm the decision scope:
   - what changes
   - what stays the same
   - which module or public surface is affected
2. Create or update `docs/ADR/ADR-XXXX-<short-title>.md`.
   - If `docs/ADR/` does not exist yet, create it.
   - If `docs/templates/ADR-Template.md` exists, use it.
   - Otherwise create the ADR with these sections:
     - `## Context`
     - `## Decision`
     - `## Diagram`
     - `## Alternatives`
     - `## Consequences`
     - `## Invariants`
     - `## Rollout And Rollback`
     - `## Verification`
     - `## Implementation Plan (step-by-step)`
3. Write the record:
   - context and why the change is needed now
   - a short decision statement
   - at least one Mermaid diagram
   - realistic alternatives with pros and cons
   - consequences, trade-offs, and mitigations
4. Make it executable:
   - include invariants that tests or static analysis must prove
   - include verification commands copied from `AGENTS.md`
   - explain how we know rollout is safe
5. Make impacts explicit:
   - code and modules affected
   - config or dependency changes
   - backward compatibility strategy
6. For this repo, anchor the ADR to real package boundaries:
   - `src/ManagedCode.MCPGateway/`
   - `tests/ManagedCode.MCPGateway.Tests/`
   - public abstractions in `Abstractions/`
   - DI registration in `Registration/`
7. If the decision changes package boundaries or search/invocation flow, update `docs/Architecture/Overview.md`.

## Guardrails

- ADRs are self-contained. Do not rely on hidden chat context.
- ADRs justify why. Feature specs describe what the system does.
- If you cannot state the decision in one or two sentences, the ADR is not ready.
