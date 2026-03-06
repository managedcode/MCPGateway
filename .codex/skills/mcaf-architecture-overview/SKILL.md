---
name: mcaf-architecture-overview
description: "Create or update `docs/Architecture/Overview.md` with Mermaid architecture diagrams, module boundaries, dependency rules, and links to ADRs/features. Use for onboarding, refactoring, module changes, or when a package/runtime boundary changes."
compatibility: "Requires repository write access; produces Markdown docs with Mermaid diagrams."
---

# MCAF: Architecture Overview

## Output

- `docs/Architecture/Overview.md` (create or update)

## Architecture Thinking (keep it a map)

This doc is the global map: boundaries, modules, and dependency rules.

- Keep it lean and structural:
  - modules/boundaries + responsibility + dependency direction
  - Mermaid diagrams are the primary context:
    - system/module map
    - interfaces/contracts map
    - key classes/types map
- Treat it as the main "start here" card for humans and AI agents:
  - diagram elements must use real names
  - every diagram element must have an explicit reference link to docs or code
  - keep diagrams readable; split them when they become spaghetti
- Keep behavior out of the overview:
  - feature flows live in `docs/Features/*`
  - decision-specific diagrams and invariants live in `docs/ADR/*`
- Anti-AI-slop rule: never invent components, services, queues, or databases. Only document what exists today or what this change explicitly adds.

## Workflow

1. Open `docs/Architecture/Overview.md` if it exists.
   - If `docs/Architecture/` does not exist yet, create it.
   - If `docs/templates/Architecture-Template.md` exists, you may start from it.
   - Otherwise create the file with these sections:
     - `## Scoping (read first)`
     - `## Summary`
     - `## System And Module Map`
     - `## Interfaces And Contracts`
     - `## Key Classes And Types`
     - `## Module Index`
     - `## Dependency Rules`
     - `## Key Decisions (ADRs)`
     - `## Related Docs`
2. Identify the real top-level boundaries:
   - entry points
   - modules/layers grouped by folders or namespaces
   - external dependencies that actually exist
3. Fill the summary so a new engineer can orient in about one minute.
4. Maintain the Mermaid diagrams:
   - system/module map: roughly 8-15 nodes, arrows labeled with calls/events/reads/writes
   - interfaces/contracts map: ports, APIs, events, queues, file formats
   - key classes/types map: only high-signal types, not an inventory
5. Fill the module index:
   - one row or bullet per diagram node
   - responsibilities and dependencies must be concrete
6. Write explicit dependency rules:
   - what is allowed
   - what is forbidden
   - how integration happens
7. Add a short ADR section:
   - link the ADRs that define boundaries, dependencies, or major cross-cutting patterns
8. Link to deeper docs:
   - ADRs for decisions
   - feature specs for behavior
   - testing/development docs when they exist
9. Keep the doc aligned with the package-first structure of this repo:
   - focus on `ManagedCode.MCPGateway`
   - reflect real runtime surfaces such as `IMcpGateway`, `McpGateway`, and `McpGatewayToolSet`
   - include `.codex/skills/` only when it is relevant to repository workflow, not runtime architecture

## Guardrails

- Do not list every file or class. This is a map, not an inventory.
- Keep the document stable and update it only when boundaries or interactions change.
- Use real commands and rules from `AGENTS.md` when the doc includes verification steps.
