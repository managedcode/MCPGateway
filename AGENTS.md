# AGENTS.md

Project: ManagedCode.MCPGateway
Stack: .NET 10, C# 14, Microsoft.Extensions.AI, ModelContextProtocol, TUnit, GitHub Actions, NuGet

Follows [MCAF](https://mcaf.managed-code.com/)

---

## Conversations (Self-Learning)

Learn the user's habits, preferences, and working style. Extract rules from conversations, save to "## Rules to follow", and generate code according to the user's personal rules.

**Update requirement (core mechanism):**

Before doing ANY task, evaluate the latest user message.
If you detect a new rule, correction, preference, or change -> update `AGENTS.md` first.
Only after updating the file you may produce the task output.
If no new rule is detected -> do not update the file.

**When to extract rules:**

- prohibition words (never, don't, stop, avoid) or similar -> add NEVER rule
- requirement words (always, must, make sure, should) or similar -> add ALWAYS rule
- memory words (remember, keep in mind, note that) or similar -> add rule
- process words (the process is, the workflow is, we do it like) or similar -> add to workflow
- future words (from now on, going forward) or similar -> add permanent rule

**Preferences -> add to Preferences section:**

- positive (I like, I prefer, this is better) or similar -> Likes
- negative (I don't like, I hate, this is bad) or similar -> Dislikes
- comparison (prefer X over Y, use X instead of Y) or similar -> preference rule

**Corrections -> update or add rule:**

- error indication (this is wrong, incorrect, broken) or similar -> fix and add rule
- repetition frustration (don't do this again, you ignored, you missed) or similar -> emphatic rule
- manual fixes by user -> extract what changed and why

**Strong signal (add IMMEDIATELY):**

- swearing, frustration, anger, sarcasm -> critical rule
- ALL CAPS, excessive punctuation (!!!, ???) -> high priority
- same mistake twice -> permanent emphatic rule
- user undoes your changes -> understand why, prevent

**Ignore (do NOT add):**

- temporary scope (only for now, just this time, for this task) or similar
- one-off exceptions
- context-specific instructions for current task only

**Rule format:**

- One instruction per bullet
- Tie to category (Testing, Code, Docs, etc.)
- Capture WHY, not just what
- Remove obsolete rules when superseded

---

## Rules to follow (Mandatory, no exceptions)

### Commands

- restore: `dotnet restore ManagedCode.MCPGateway.slnx`
- build: `dotnet build ManagedCode.MCPGateway.slnx -c Release --no-restore`
- analyze: `dotnet build ManagedCode.MCPGateway.slnx -c Release --no-restore -p:RunAnalyzers=true`
- test: `dotnet test --solution ManagedCode.MCPGateway.slnx -c Release --no-build`
- format: `dotnet format ManagedCode.MCPGateway.slnx`
- skills-validate: `python3 .codex/skills/mcaf-skill-curation/scripts/validate_skills.py .codex/skills`
- skills-metadata: `python3 .codex/skills/mcaf-skill-curation/scripts/generate_available_skills.py .codex/skills --absolute`

### Task Delivery (ALL TASKS)

- Always keep package and project identity as `ManagedCode.MCPGateway`.
- Always use `Microsoft.Extensions.AI` and the official `ModelContextProtocol` .NET SDK as the integration foundation.
- Never introduce Microsoft Agentic Framework into this repository unless the user explicitly re-opens that requirement.
- Start from the root docs and packaging files before making structural changes:
  - `README.md`
  - `Directory.Build.props`
  - `Directory.Packages.props`
  - `global.json`
  - `.github/workflows/*`
- Keep scope explicit before coding:
  - in scope
  - out of scope
- Implement code and tests together for every behavior change.
- Keep the gateway reusable as a NuGet library, not as an app-specific host.
- Preserve one public execution surface for local `AITool` instances and MCP tools.
- Preserve one searchable catalog that supports vector ranking when embeddings are available and lexical fallback when they are not.
- Keep meta-tools available through `McpGatewayToolSet` and `IMcpGateway.CreateMetaTools(...)`.
- If a user adds or corrects a persistent workflow rule, update `AGENTS.md` first and only then continue with the task.

### Repository Layout

- `src/ManagedCode.MCPGateway/` contains the package source.
- `tests/ManagedCode.MCPGateway.Tests/` contains integration-style package tests.
- `src/ManagedCode.MCPGateway/Abstractions/` contains public interfaces.
- `src/ManagedCode.MCPGateway/Models/` contains public contracts and internal source registrations.
- `src/ManagedCode.MCPGateway/Registration/` contains DI registration extensions.
- `src/ManagedCode.MCPGateway/McpGateway.cs` is the main runtime implementation.
- `src/ManagedCode.MCPGateway/McpGatewayToolSet.cs` exposes the gateway as reusable `AITool` meta-tools.
- `.codex/skills/` contains repo-local MCAF skills for Codex.

### Skills (ALL TASKS)

- Keep repo-local MCAF skills under `.codex/skills/`, not in ad-hoc folders.
- Keep one workflow per skill folder with a required `SKILL.md`.
- Keep skill metadata concise and fix the YAML `description` when a skill mis-triggers.
- Keep skill folders lean: only `SKILL.md`, `scripts/`, `references/`, `assets/`, and agent metadata when needed.
- Validate skills after skill changes with `skills-validate`.
- Regenerate the available-skills metadata block with `skills-metadata` when skill inventory or metadata changes.

### Documentation (ALL TASKS)

- Update `README.md` whenever public API shape, setup, or usage changes.
- Keep the README focused on package usage and onboarding, not internal implementation notes.
- Document optional DI dependencies explicitly in README examples so consumers know which services they must register themselves, such as embedding generators.
- Keep README code examples as real example code blocks, not commented-out pseudo-code; if behavior is optional, show it in a separate example instead of commenting lines inside another snippet.
- Never leave empty placeholder setup blocks in README examples such as `// gateway configuration`; show a concrete minimal configuration that actually demonstrates the API.
- Keep repo docs and skills in English to stay aligned with MCAF conventions.
- Keep root packaging metadata centralized in `Directory.Build.props`.
- Keep package versions centralized in `Directory.Packages.props`.
- Keep workflow logic only in `.github/workflows/`.

### Testing (ALL TASKS)

- Test framework in this repository is TUnit. Never add or keep xUnit here.
- For TUnit solution runs, always invoke `dotnet test --solution ...`; do not pass the solution path positionally.
- Every behavior change must include or update tests in `tests/ManagedCode.MCPGateway.Tests/`.
- Keep tests focused on real gateway behavior:
  - local tool indexing and invocation
  - MCP tool indexing and invocation
  - vector search behavior
  - lexical fallback behavior
- Keep embedding-based search covered with deterministic local tests by using a fake or test-only embedding generator.
- Keep request context behavior covered when search or invocation consumes contextual inputs.
- Do not remove tests to get green builds.
- Keep `global.json` configured for `Microsoft.Testing.Platform` when TUnit is used.
- Run verification in this order:
  - restore
  - build
  - test

### Code Style

- Follow `.editorconfig` and repository analyzers.
- Keep warnings clean; repository builds treat warnings as errors.
- Prefer simple, readable C# over clever abstractions.
- Keep public API names aligned with package identity `ManagedCode.MCPGateway`.
- Do not duplicate package metadata or version blocks inside project files unless a project-specific override is required.
- Use constants for stable tool names and protocol-facing identifiers.
- Never leave stable string literals inline in runtime code; extract named constants for diagnostic codes, messages, modes, keys, and other durable identifiers so changes stay centralized.
- Keep transport-specific logic inside the gateway and source registration abstractions, not scattered across the codebase.
- Keep the package dependency surface small and justified.

### Critical (NEVER violate)

- Never rename the package away from `ManagedCode.MCPGateway` without explicit user approval.
- Never add Microsoft Agentic Framework dependencies unless explicitly requested by the user.
- Never publish to NuGet from the local machine without explicit user confirmation.
- Never use destructive git commands without explicit user approval.
- Never weaken tests, analyzers, or packaging checks to make CI pass.

### Boundaries

**Always:**

- Read `AGENTS.md` before editing code.
- Keep the repository package-first and library-first.
- Keep the gateway generic; do not bake in AIBase-specific or Orleans-specific runtime assumptions.

**Ask first:**

- breaking public API changes
- new runtime dependencies
- package metadata changes visible to consumers
- release version changes
- publish/release actions

---

## Preferences

### Likes

- Explicit package structure
- Reusable library design over app-specific glue
- Search + execute flows covered by automated tests
- Clean root packaging and CI setup

### Dislikes

- Agentic Framework dependency creep in this repository
- App-specific logic leaking into the shared gateway package
- Duplicate metadata and versions across multiple files
- Shipping behavior without tests
