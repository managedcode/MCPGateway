---
name: mcaf-feature-spec
description: "Create or update a feature spec under `docs/Features/` with business rules, user flows, system behavior, Mermaid diagrams, verification, and Definition of Done. Use before non-trivial implementation or when behavior changes and the repo needs an executable spec."
compatibility: "Requires repository write access; produces Markdown docs with Mermaid diagrams and executable verification steps."
---

# MCAF: Feature Spec

## Outputs

- `docs/Features/<feature>.md` (create or update)
- update links from or to ADRs and architecture docs when needed

## Spec Quality

Write a spec that can be implemented and verified without guessing:

- no placeholders such as `TBD`, `later`, or `etc.`
- use real module and type names from the codebase
- make business rules testable with clear inputs and outputs
- make flows executable with preconditions, steps, and expected results
- copy verification commands from `AGENTS.md`
- include enough detail for Product, Dev, QA, and DevOps to ship safely

## Workflow

1. Start from `docs/Architecture/Overview.md` when it exists so you can identify the affected module boundaries.
2. Create or update `docs/Features/<feature>.md`.
   - If `docs/Features/` does not exist yet, create it.
   - If `docs/templates/Feature-Template.md` exists, use it.
   - Otherwise create a lean spec with these sections:
     - `## Purpose And Scope`
     - `## Affected Modules`
     - `## Business Rules`
     - `## Main Flow`
     - `## Negative And Edge Cases`
     - `## System Behavior`
     - `## Verification`
     - `## Definition Of Done`
     - `## Implementation Plan (step-by-step)`
3. Define behavior precisely:
   - purpose and scope
   - numbered business rules
   - primary flow and edge cases
4. Describe system behavior in terms of entry points, reads/writes, side effects, idempotency, and errors.
5. Add a Mermaid diagram for the main flow and keep it readable.
6. Write verification that can actually be executed:
   - environment assumptions
   - concrete positive, negative, and edge test flows
   - mapping to real tests or planned test locations
   - use the canonical commands from `AGENTS.md`
7. Keep the Definition of Done strict:
   - automated tests cover the behavior
   - analyzers and build stay clean
   - docs are updated when boundaries or decisions changed
8. For this repo, make the spec repository-aware:
   - use `ManagedCode.MCPGateway` package terms and public API names
   - when the feature affects tool indexing/search/invocation, name the exact types and paths involved
   - if tests are part of the change, reference `tests/ManagedCode.MCPGateway.Tests/`

## Guardrails

- If the feature introduces a new dependency, runtime boundary, or public API pattern, write an ADR and update the architecture overview.
- Do not hide architecture decisions inside the feature doc. Decisions go to ADRs.
