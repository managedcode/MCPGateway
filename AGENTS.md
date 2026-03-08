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
- test-list: `dotnet test --solution ManagedCode.MCPGateway.slnx -c Release --no-build --list-tests`
- test-detailed: `dotnet test --solution ManagedCode.MCPGateway.slnx -c Release --no-build --output Detailed --no-progress`
- test-trx: `dotnet test --solution ManagedCode.MCPGateway.slnx -c Release --no-build --report-trx --results-directory ./artifacts/test-results`
- test-runner-help: `tests/ManagedCode.MCPGateway.Tests/bin/Release/net10.0/ManagedCode.MCPGateway.Tests --help`
- format: `dotnet format ManagedCode.MCPGateway.slnx`

### Rule Precedence

- Read the solution-root `AGENTS.md` first for every task.
- Read the nearest project-local `AGENTS.md` after the root file when the task touches a specific project subtree.
- Apply the stricter rule when root and local guidance overlap.
- If a local rule appears weaker than the root policy, stop and clarify it before editing code.
- Record justified exceptions in the nearest durable doc such as a local `AGENTS.md`, ADR, or feature doc.

### Global Skills

- Core .NET routing: `mcaf-dotnet`, `mcaf-dotnet-features`, `mcaf-testing`, `mcaf-dotnet-tunit`
- Quality and maintainability: `mcaf-dotnet-quality-ci`, `mcaf-dotnet-complexity`, `mcaf-solid-maintainability`
- Governance and docs: `mcaf-solution-governance`, `mcaf-architecture-overview`, `mcaf-adr-writing`, `mcaf-feature-spec`, `mcaf-ci-cd`
- Repo-local extras: `cloc`, `dotnet-strict`, `pre-pr`, `profile`, `quickdup`, `roslynator`

### Maintainability Limits

- `file_max_loc`: `650`
- `type_max_loc`: `350`
- `function_max_loc`: `90`
- `max_nesting_depth`: `4`
- `exception_policy`: Temporary limit breaches require an explicit justification in the nearest durable doc and a follow-up path to remove the debt.

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
- For multilingual or noisy search inputs, prefer a generic English-normalization step before ranking when an AI/query-rewrite component is available, because the user wants the searchable representation to converge to English instead of relying only on language-specific token overlap.
- Keep meta-tools available through `McpGatewayToolSet` and `IMcpGateway.CreateMetaTools(...)`.
- If a user adds or corrects a persistent workflow rule, update `AGENTS.md` first and only then continue with the task.

### Repository Layout

- `src/ManagedCode.MCPGateway/` contains the package source.
- `tests/ManagedCode.MCPGateway.Tests/` contains integration-style package tests.
- `src/ManagedCode.MCPGateway/Abstractions/` contains public interfaces, grouped by concern when needed.
- `src/ManagedCode.MCPGateway/Configuration/` contains public configuration types and service keys.
- `src/ManagedCode.MCPGateway/Models/` contains public contracts grouped by behavior such as search, invocation, catalog, and embeddings.
- `src/ManagedCode.MCPGateway/Embeddings/` contains public embedding-store implementations.
- `src/ManagedCode.MCPGateway/Internal/` contains internal catalog, runtime, and helper implementation details.
- `src/ManagedCode.MCPGateway/Registration/` contains DI registration extensions.
- `src/ManagedCode.MCPGateway/McpGateway.cs` is the public gateway facade.
- `src/ManagedCode.MCPGateway/Internal/Runtime/` contains the internal runtime orchestration implementation, grouped by core, catalog, search, invocation, and embeddings responsibilities.
- `src/ManagedCode.MCPGateway/McpGatewayToolSet.cs` exposes the gateway as reusable `AITool` meta-tools.
- `.codex/skills/` contains repo-local MCAF skills for Codex.
- Keep the source tree explicitly modular: separate public API folders from `Internal/` implementation folders, and group runtime classes by responsibility in dedicated folders instead of dumping search, indexing, invocation, registry, and infrastructure files into the package root, because flat structure hides boundaries and invites god-object design.

### Skills (ALL TASKS)

- Bootstrap or refresh MCAF skills from the canonical tutorial and raw GitHub skill folders; do not rely on a shell installer because MCAF `v1.2` is URL-first.
- Keep repo-local MCAF skills under `.codex/skills/`, not in ad-hoc folders.
- Keep one workflow per skill folder with a required `SKILL.md`.
- Keep skill metadata concise and fix the YAML `description` when a skill mis-triggers.
- Keep skill folders lean: only `SKILL.md`, `scripts/`, `references/`, `assets/`, and agent metadata when needed.
- Do not reference or depend on `mcaf-skill-curation` in this repository until the skill is intentionally added back, because the folder is absent here and stale references break the workflow.

### Documentation (ALL TASKS)

- Update `README.md` whenever public API shape, setup, or usage changes.
- For non-trivial architecture, runtime-flow, or cross-cutting search changes, always add or update an ADR under `docs/ADR/`, update `docs/Architecture/Overview.md`, and keep `README.md` synchronized with the shipped behavior and examples so the docs describe the real package rather than an older design snapshot.
- When the package requires an initialization step such as index building, provide an ergonomic optional integration path (for example DI extension or hosted background warmup) instead of forcing every consumer to call it manually, and document when manual initialization is still appropriate.
- Keep documented configuration defaults synchronized with the actual `McpGatewayOptions` defaults; for example, `MaxSearchResults` default is `15`, not stale sample values.
- Keep the README focused on package usage and onboarding, not internal implementation notes.
- Keep `README.md` free of unnecessary internal detail; it should stay clear, example-driven, and focused on what consumers need to understand and use the package quickly.
- Document optional DI dependencies explicitly in README examples so consumers know which services they must register themselves, such as embedding generators.
- Keep README code examples as real example code blocks, not commented-out pseudo-code; if behavior is optional, show it in a separate example instead of commenting lines inside another snippet.
- Never leave empty placeholder setup blocks in README examples such as `// gateway configuration`; show a concrete minimal configuration that actually demonstrates the API.
- Keep repo docs and skills in English to stay aligned with MCAF conventions.
- Keep root packaging metadata centralized in `Directory.Build.props`.
- Keep package versions centralized in `Directory.Packages.props`.
- Keep workflow logic only in `.github/workflows/`.

### Testing (ALL TASKS)

- Test framework in this repository is TUnit. Never add or keep xUnit here.
- This repository uses `TUnit` on `Microsoft.Testing.Platform`; never use VSTest-only flags such as `--filter` or `--logger`, because they are not supported here.
- For TUnit solution runs, always invoke `dotnet test --solution ...`; do not pass the solution path positionally.
- Every behavior change must include or update tests in `tests/ManagedCode.MCPGateway.Tests/`.
- Add tests only when they close a meaningful behavior or regression gap; avoid low-signal tests that only increase count without improving confidence.
- Keep tests focused on real gateway behavior:
  - local tool indexing and invocation
  - MCP tool indexing and invocation
  - vector search behavior
  - lexical fallback behavior
- Keep embedding-based search covered with deterministic local tests by using a fake or test-only embedding generator.
- Keep request context behavior covered when search or invocation consumes contextual inputs.
- Do not remove tests to get green builds.
- Keep `global.json` configured for `Microsoft.Testing.Platform` when TUnit is used.
- At the end of implementation work, run code-size and quality verification with `cloc`, `roslynator`, and the repository's strict .NET build/test checks, then fix actionable findings so oversized files and quality drift do not accumulate.
- Run verification in this order:
  - restore
  - build
  - test

### Code Style

- Follow `.editorconfig` and repository analyzers.
- Keep warnings clean; repository builds treat warnings as errors.
- Always treat local and CI builds as `WarningsAsErrors`; never rely on warnings being acceptable, because this repository expects zero-warning output as a hard quality gate.
- Prefer simple, readable C# over clever abstractions.
- Prefer modern C# 14 syntax when it improves clarity and keep replacing stale legacy syntax with current idiomatic language constructs instead of preserving older forms by inertia.
- Prefer straightforward DI-native constructors in public types; avoid redundant constructor chaining that only wraps `new SomeRuntime(...)` behind a second constructor, because in modern C# this adds ceremony without improving clarity.
- In hot runtime paths, prefer single-pass loops over allocation-heavy LINQ chains when the logic is simple, because duplicate enumeration and transient allocations have already been called out as unacceptable in this repository.
- Avoid open-ended `while (true)` loops in runtime code when a real termination condition exists; use an explicit condition such as cancellation or lifecycle state so concurrency code stays auditable.
- Avoid transient collection + `string.Join` assembly in hot runtime string paths; build the final string directly when only a few optional segments exist, because these extra allocations have already been called out as wasteful in this repository.
- Prefer readable imperative conditionals over long multi-line boolean expression bodies; if a predicate stops being obvious at a glance, split it into guard clauses or named locals instead of compressing it into one chained return expression.
- Prefer non-blocking coordination over coarse locking when practical; use concurrent collections, atomic state, and single-flight patterns instead of `lock`-heavy designs, because blocking synchronization has already proven to obscure concurrency behavior in this package.
- Keep concurrency coordination intention-revealing: avoid opaque fields such as generic drain/task signals inside runtime services when a named helper or clearer lifecycle abstraction can express the behavior, because hidden synchronization state quickly turns registry/runtime code into unreadable infrastructure.
- Prefer serializer-first JSON/schema handling; avoid ad-hoc manual special cases for `JsonElement`/`JsonNode`/schema objects when normal `System.Text.Json` serialization can represent them correctly.
- For JSON and schema payloads, always route serialization through the repository's canonical JSON converter/options path; do not hand-roll ad-hoc `JsonSerializer.Serialize*` handling inside feature code when the package already defines how JSON should be materialized.
- For context/object flattening, do not maintain parallel per-type serialization trees by hand; normalize once through the canonical JSON path and traverse the normalized representation, because duplicated type-switch logic drifts and keeps reintroducing ad-hoc serialization.
- Prefer explicit SOLID object decomposition over large `partial` types; when responsibilities like registry, indexing, invocation, or schema handling can live in dedicated classes, extract real collaborators instead of only splitting files.
- Keep `McpGateway` focused on search/invoke orchestration only; do not embed registry or mutation responsibilities into the gateway type itself, because that mixes lifecycle/catalog mutation with runtime execution concerns.
- Keep public API names aligned with package identity `ManagedCode.MCPGateway`.
- For package-scoped public API members, prefer concise names without repeating the `ManagedCode` brand inside method names when the namespace/package already scopes them, because redundant branding makes the API noisy.
- Do not duplicate package metadata or version blocks inside project files unless a project-specific override is required.
- Use constants for stable tool names and protocol-facing identifiers.
- Never leave stable string literals inline in runtime code; extract named constants for diagnostic codes, messages, modes, keys, tool descriptions, and other durable identifiers so changes stay centralized.
- Use the correct contextual logger type for each service; internal collaborators must log with their own type category instead of reusing a parent facade logger, because wrong logger categories make diagnostics misleading.
- Keep transport-specific logic inside the gateway and source registration abstractions, not scattered across the codebase.
- Keep the package dependency surface small and justified.
- Prefer direct generic DI registrations such as `services.TryAddSingleton<IService, Implementation>()` over lambda alias registrations when wiring package services, because the lambda style has already been called out as unreadable and error-prone in this repository.
- Keep runtime services DI-native from their public/internal constructors; types such as `McpGatewayRegistry` must be creatable through `IOptions<McpGatewayOptions>` and other DI-managed dependencies rather than ad-hoc state-only constructors, because the package design requires services to live fully inside the container.
- When emitting package identity to external protocols such as MCP client info, never hardcode a fake version string; use the actual assembly/build version so runtime metadata stays aligned with the package being shipped.
- For search-quality improvements, prefer mathematical or statistical ranking changes over hardcoded phrase lists or ad-hoc query text hacks, because the user explicitly wants tokenizer search to improve through general scoring behavior rather than manual exceptions.
- Prefer framework-provided in-memory caching primitives such as `IMemoryCache` over custom process-local storage implementations when they cover the lifecycle and lookup needs, because self-rolled memory stores age poorly and make scaling/concurrency behavior harder to trust.
- Never keep legacy compatibility shims, obsolete paths, or lingering documentation references to removed implementations when a replacement is accepted, because this repository should converge on the current design instead of carrying dead historical baggage.
- Never leave `ManagedCode`-prefixed DI/setup extension method names such as `AddManagedCodeMcpGateway(...)` in the public API once concise `McpGateway` naming is available, because these branded leftovers make the package surface inconsistent and read like stale legacy.

### Critical (NEVER violate)

- Never rename the package away from `ManagedCode.MCPGateway` without explicit user approval.
- Never add Microsoft Agentic Framework dependencies unless explicitly requested by the user.
- Never publish to NuGet from the local machine without explicit user confirmation.
- Never use destructive git commands without explicit user approval.
- Never weaken tests, analyzers, or packaging checks to make CI pass.
- This repository uses `TUnit` on top of `Microsoft.Testing.Platform`, so prefer the `dotnet test --solution ...` commands above. Do not assume VSTest-only flags such as `--filter` or `--logger` are available here.


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
- Direct fixes over preserving legacy compatibility paths when cleanup or review-driven corrections are requested
- Framework-provided caching primitives over self-rolled in-memory stores when the package only needs process-local cache semantics
- Removing replaced code paths completely instead of keeping legacy mentions or compatibility leftovers
- Concise `McpGateway` public registration/init API names without leftover `ManagedCode` branding

### Dislikes

- Agentic Framework dependency creep in this repository
- App-specific logic leaking into the shared gateway package
- Duplicate metadata and versions across multiple files
- Shipping behavior without tests
- Self-rolled in-memory storage when standard .NET caching abstractions already fit the scenario
- Legacy/obsolete compatibility leftovers after a replacement is accepted
- `ManagedCode`-prefixed public DI/setup API names that should have been cleaned up
