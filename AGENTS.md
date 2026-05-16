# AGENTS.md

Project: ManagedCode.MCPGateway
Stack: .NET 10, C# 14, Microsoft.Extensions.AI, ModelContextProtocol, TUnit, GitHub Actions, NuGet

Follows [MCAF](https://mcaf.managed-code.com/)

---

## Purpose

This file defines how AI agents work in this solution.

- Root `AGENTS.md` holds the global workflow, shared commands, cross-cutting rules, and global skill routing.
- The solution keeps project-local `AGENTS.md` files for the package and test projects so work can stay scoped.
- Local `AGENTS.md` files refine entry points, boundaries, commands, and risks for their subtree without weakening root policy.

## Solution Topology

- Solution root: `.`
- Projects or modules with local `AGENTS.md` files:
  - `src/ManagedCode.MCPGateway/`
  - `benchmarks/ManagedCode.MCPGateway.Benchmarks/`
  - `tests/ManagedCode.MCPGateway.Tests/`

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
- tool-restore: `dotnet tool restore`
- build: `dotnet build ManagedCode.MCPGateway.slnx -c Release --no-restore`
- analyze: `dotnet build ManagedCode.MCPGateway.slnx -c Release --no-restore -p:RunAnalyzers=true`
- test: `dotnet test --solution ManagedCode.MCPGateway.slnx -c Release --no-build`
- test-list: `dotnet test --solution ManagedCode.MCPGateway.slnx -c Release --no-build --list-tests`
- test-detailed: `dotnet test --solution ManagedCode.MCPGateway.slnx -c Release --no-build --output Detailed --no-progress`
- test-trx: `dotnet test --solution ManagedCode.MCPGateway.slnx -c Release --no-build --report-trx --results-directory ./artifacts/test-results`
- test-runner-help: `tests/ManagedCode.MCPGateway.Tests/bin/Release/net10.0/ManagedCode.MCPGateway.Tests --help`
- format: `dotnet format ManagedCode.MCPGateway.slnx`
- format-check: `dotnet format ManagedCode.MCPGateway.slnx --verify-no-changes`
- roslynator-analyze: `dotnet tool run roslynator analyze src/ManagedCode.MCPGateway/ManagedCode.MCPGateway.csproj tests/ManagedCode.MCPGateway.Tests/ManagedCode.MCPGateway.Tests.csproj`
- coverage: `dotnet tool run coverlet tests/ManagedCode.MCPGateway.Tests/bin/Release/net10.0/ManagedCode.MCPGateway.Tests.dll --target "./tests/ManagedCode.MCPGateway.Tests/bin/Release/net10.0/ManagedCode.MCPGateway.Tests" --targetargs "" --format cobertura --output artifacts/coverage/coverage.cobertura.xml`
- coverage-report: `dotnet tool run reportgenerator -reports:"artifacts/coverage/coverage.cobertura.xml" -targetdir:"artifacts/coverage-report" -reporttypes:"HtmlSummary;MarkdownSummaryGithub"`
- benchmark: `dotnet run -c Release --project benchmarks/ManagedCode.MCPGateway.Benchmarks/ManagedCode.MCPGateway.Benchmarks.csproj -- --filter "*"`
- benchmark-search: `dotnet run -c Release --project benchmarks/ManagedCode.MCPGateway.Benchmarks/ManagedCode.MCPGateway.Benchmarks.csproj -- --filter "*Search*"`
- benchmark-index: `dotnet run -c Release --project benchmarks/ManagedCode.MCPGateway.Benchmarks/ManagedCode.MCPGateway.Benchmarks.csproj -- --filter "*Index*"`
- benchmark-tools: `dotnet run -c Release --project benchmarks/ManagedCode.MCPGateway.Benchmarks/ManagedCode.MCPGateway.Benchmarks.csproj -- --filter "*ToolSet*"`

### Rule Precedence

- Read the solution-root `AGENTS.md` first for every task.
- Read the nearest project-local `AGENTS.md` after the root file when the task touches a specific project subtree.
- Apply the stricter rule when root and local guidance overlap.
- If a local rule appears weaker than the root policy, stop and clarify it before editing code.
- Record justified exceptions in the nearest durable doc such as a local `AGENTS.md`, ADR, or feature doc.

### Project AGENTS Policy

- Keep one root `AGENTS.md` plus one local `AGENTS.md` in each project or module root that needs tighter scope.
- Each local `AGENTS.md` must document project purpose, entry points, boundaries, project-local commands, applicable skills, and protected or high-risk areas.
- If a project grows enough that the root file becomes vague for that subtree, tighten or add the local `AGENTS.md` before continuing implementation.

### Global Skills

- Keep the standardized workflow skills first; use the extra installed inventory only when the repository actually wires that tool into commands, CI, docs, or an explicit user request.
- Core .NET routing: `mcaf-dotnet`, `mcaf-dotnet-features`, `mcaf-testing`, `mcaf-dotnet-tunit`
- Standardized .NET toolchain: `mcaf-dotnet-analyzer-config`, `mcaf-dotnet-code-analysis`, `mcaf-dotnet-format`, `mcaf-dotnet-roslynator`, `mcaf-dotnet-coverlet`, `mcaf-dotnet-reportgenerator`, `mcaf-dotnet-codeql`
- Extended .NET catalog: `mcaf-dotnet-archunitnet`, `mcaf-dotnet-coverlet`, `mcaf-dotnet-csharpier`, `mcaf-dotnet-meziantou-analyzer`, `mcaf-dotnet-mstest`, `mcaf-dotnet-netarchtest`, `mcaf-dotnet-reportgenerator`, `mcaf-dotnet-semgrep`, `mcaf-dotnet-stryker`, `mcaf-dotnet-stylecop-analyzers`, `mcaf-dotnet-xunit`
- Quality and maintainability: `mcaf-dotnet-quality-ci`, `mcaf-dotnet-complexity`, `mcaf-solid-maintainability`
- Governance and docs: `mcaf-solution-governance`, `mcaf-architecture-overview`, `mcaf-adr-writing`, `mcaf-feature-spec`, `mcaf-ci-cd`
- Delivery and review: `mcaf-agile-delivery`, `mcaf-code-review`, `mcaf-devex`, `mcaf-documentation`, `mcaf-human-review-planning`, `mcaf-source-control`
- Cross-cutting quality: `mcaf-nfr`, `mcaf-observability`, `mcaf-security-baseline`
- Domain-specific optional skills: `mcaf-ml-ai-delivery`, `mcaf-ui-ux`
- Repo-local extras: `cloc`, `dotnet-strict`, `pre-pr`, `profile`, `quickdup`, `roslynator`

### Maintainability Limits

- `file_max_loc`: `650`
- `type_max_loc`: `350`
- `function_max_loc`: `90`
- `max_nesting_depth`: `4`
- `exception_policy`: Temporary limit breaches require an explicit justification in the nearest durable doc and a follow-up path to remove the debt.

### Task Delivery (ALL TASKS)

- Start from `docs/Architecture/Overview.md` and the nearest project-local `AGENTS.md` for every non-trivial task.
- Treat `docs/Architecture/Overview.md` as the architecture map for this solution; if it becomes stale for the changed area, update it in the same task.
- Always keep package and project identity as `ManagedCode.MCPGateway`.
- Treat the official `modelcontextprotocol/csharp-sdk` repository and shipped feature surface as the MCP baseline for this package, because `ManagedCode.MCPGateway` is built on top of that SDK rather than on a narrower custom protocol layer.
- When the SDK already supports an MCP capability, prefer exposing and integrating that capability in `ManagedCode.MCPGateway` instead of assuming the missing surface is intentionally unsupported, unless the user explicitly excludes it.
- HTTP MCP server registrations must preserve the `Http` source kind while using the official MCP C# SDK HTTP transport; default to Streamable HTTP for modern remote endpoints, expose SDK transport knobs through an options object for future host needs, and do not replace HTTP sources with custom-client workarounds, positional-overload sprawl, or hand-written JSON-RPC/SSE transport code.
- When the user asks for parity with the official `modelcontextprotocol/csharp-sdk`, treat the entire SDK feature surface as in scope for `ManagedCode.MCPGateway`, including capabilities that the SDK currently marks experimental, unless the user explicitly narrows that scope.
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
- Preserve one searchable catalog that uses Markdown-LD graph ranking by default and supports vector ranking only when embeddings are explicitly selected.
- Tool search must support sparse high-confidence selection plus an explicit related/next-step expansion path; do not make consumers pass the full tool catalog when a smaller capability set can answer the request.
- For multilingual or noisy search inputs, prefer a generic English-normalization step before ranking when an AI/query-rewrite component is available, because the user wants the searchable representation to converge to English instead of relying only on language-specific token overlap.
- For multilingual, typo-heavy, or noisy search inputs, keep confidence calibration strict: clearly weak or irrelevant matches must not surface with saturated `Score = 1`, because inflated certainty hides ranking defects and breaks tool selection trust.
- When emitting telemetry for vector search or vector-backed index building, include token-usage metrics alongside duration so operators can see vector cost as well as latency.
- Keep meta-tools available through `McpGatewayToolSet` and `IMcpGateway.CreateMetaTools(...)`.
- When Markdown-LD graph search is selected, startup or explicit index initialization must build and validate the tool graph before search/tool discovery so LLM-facing MCP tool selection is based on the correct focused graph.
- Markdown-LD graph search must support both startup-generated graphs and filesystem-provided graph files; tests for file-backed graph mode must generate the graph fixture through the package flow rather than relying on a hand-authored static artifact.
- If a user adds or corrects a persistent workflow rule, update `AGENTS.md` first and only then continue with the task.
- When a search-quality fix is requested as a concrete architectural change, finish the intended runtime behavior in the same task instead of stopping at a partial scoring-only step, because partial search fixes leave the real retrieval defect unresolved.
- When the user asks for a shipped feature set, implement the runtime behavior end-to-end with production-ready code and tests instead of leaving placeholder, mocked, or temporary execution paths, because partial delivery is explicitly rejected in this repository.
- During stabilization and release-hardening work, proactively search for analogous fragile patterns after fixing the first defect, and fix every confirmed issue with the correct SDK, platform, or .NET primitive plus tests instead of hacks, suppressions, or narrow one-off reactions.
- Do not hide runtime or transport timeouts as magic numbers in package code; when a timeout is required, expose it as an explicit `TimeSpan` option with a clear default, caller override, and validation. Test-only bounded waits are acceptable only as harness hang guards, not as product behavior.
- When adopting new upstream graph/search capabilities such as `ManagedCode.MarkdownLd.Kb` schema-aware search, implement the real hybrid runtime benefits with tests and docs instead of limiting the task to dependency bumps or export helpers, because the user expects the package to capture the upstream search value end-to-end.
- When adding federated graph search, do not reject federation as a category; expose it through explicit allowlisted APIs or built-in tools with diagnostics and tests, because the user wants powerful hybrid/federated search while keeping hidden unconfigured network calls out of the default path.
- Keep graph/search/index-specific capabilities on the appropriate search or indexing public surface instead of forcing them directly onto the MCP-facing `IMcpGateway` facade, because graph federation and graph export are search/index concerns rather than generic MCP gateway operations.
- Markdown-LD graph search must be SPARQL-backed when schema-aware graph search is enabled; use `ManagedCode.MarkdownLd.Kb` schema search/federated SPARQL as the primary graph retrieval path and keep token-distance or vector signals as hybrid ranking/supporting signals rather than as a replacement for graph SPARQL.
- For graph/search delivery, do not rely on old mock-only coverage to prove retrieval quality; prefer real gateway integration tests that exercise generated Markdown-LD/SPARQL graph behavior, MCP-backed graph tools, and explicit vector-enabled paths so tests prove the shipped graph-first runtime instead of a mocked approximation.
- Built-in graph/search tooling must include explicit schema/profile and index-build/inspection tools, because consumers and LLM agents need to understand, rebuild, and validate the tool graph/index before relying on graph/SPARQL retrieval.
- For performance optimization work, add or update BenchmarkDotNet benchmarks with allocation statistics and use the benchmark results to choose hot-path changes; avoid speculative `ValueTask`, `struct`, `AsSpan`, compiled-lambda, or collection rewrites unless they improve measured runtime or allocation behavior without hurting API clarity.
- CI and release benchmark coverage must run the full BenchmarkDotNet benchmark suite rather than smoke-only or shortcut benchmark checks, because benchmark results are used as real performance evidence in this repository.
- README benchmark documentation must include the latest representative benchmark result snapshot when benchmark coverage or performance behavior changes, because the user expects README readers to see actual measured results, not only benchmark commands.

### Repository Layout

- `src/ManagedCode.MCPGateway/` contains the package source.
- `benchmarks/ManagedCode.MCPGateway.Benchmarks/` contains BenchmarkDotNet performance and allocation benchmarks.
- `tests/ManagedCode.MCPGateway.Tests/` contains integration-style package tests.
- `src/ManagedCode.MCPGateway/Gateway/` contains the public gateway facade, factory, options, DI entry points, runtime core, telemetry, and serialization.
- `src/ManagedCode.MCPGateway/Discovery/` contains reusable `AITool` meta-tools, auto-discovery chat integration, and discovery-specific registration/configuration.
- `src/ManagedCode.MCPGateway/Catalog/` contains catalog contracts, models, registration state, descriptors, source adapters, and index-building logic.
- `src/ManagedCode.MCPGateway/Search/` contains search contracts, models, caching, embeddings, and internal ranking/graph/normalization/context logic.
- `src/ManagedCode.MCPGateway/Invocation/` contains invocation models and runtime execution helpers.
- `src/ManagedCode.MCPGateway/Prompts/` contains prompt contracts, models, and prompt catalog implementation.
- `src/ManagedCode.MCPGateway/Hosting/` contains MCP server export and warmup integration.
- `.codex/skills/` contains repo-local MCAF skills for Codex.
- Keep the source tree explicitly modular: separate public API folders from `Internal/` implementation folders, and group runtime classes by responsibility in dedicated folders instead of dumping search, indexing, invocation, registry, and infrastructure files into the package root, because flat structure hides boundaries and invites god-object design.
- Prefer vertical-slice package structure for non-trivial areas: group code by feature and subfeature with isolated supporting types nearby instead of accumulating broad cross-cutting buckets that mix unrelated behaviors in one folder, because feature-local structure keeps boundaries auditable.

### Skills (ALL TASKS)

- Bootstrap or refresh MCAF skills from the canonical tutorial and raw GitHub skill folders; do not rely on a shell installer because MCAF `v1.2` is URL-first.
- For MCAF skill refresh tasks, treat the current tutorial as the source of truth for which skill folders should exist locally; keep the baseline bundle from the tutorial and add tool-specific `.NET` skills only when this repository is actually standardized on them.
- When the user explicitly asks for the full `.NET` MCAF catalog, install every available `mcaf-dotnet*` skill folder from upstream even if the tutorial baseline for a minimal setup is smaller.
- When the user explicitly confirms that the required skill folders are already present, do not re-add, prune, or resync the skill inventory again unless they ask for another inventory change, because governance sync and skill inventory sync are separate tasks in this repository.
- Keep repo-local MCAF skills under `.codex/skills/`, not in ad-hoc folders.
- Keep one workflow per skill folder with a required `SKILL.md`.
- Keep skill metadata concise and fix the YAML `description` when a skill mis-triggers.
- Keep skill folders lean: only `SKILL.md`, `scripts/`, `references/`, `assets/`, and agent metadata when needed.
- Do not reference or depend on `mcaf-skill-curation` in this repository until the skill is intentionally added back, because the folder is absent here and stale references break the workflow.

### Documentation (ALL TASKS)

- Update `README.md` whenever public API shape, setup, or usage changes.
- When a README refresh is requested, remove stale sections and replace them with the current shipped behavior instead of appending changelog-style notes, because the user wants the README to read as the authoritative current guide.
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
- Keep simple XML elements in packaging files on one line when they fit naturally, and never leave broken wrapped tags like split `Description` elements in `Directory.Build.props`, because the user explicitly rejects that formatting.
- Keep package versions centralized in `Directory.Packages.props`.
- Keep workflow logic only in `.github/workflows/`.

### Testing (ALL TASKS)

- Test framework in this repository is TUnit. Never add or keep xUnit here.
- This repository uses `TUnit` on `Microsoft.Testing.Platform`; never use VSTest-only flags such as `--filter` or `--logger`, because they are not supported here.
- For TUnit solution runs, always invoke `dotnet test --solution ...`; do not pass the solution path positionally.
- Coverage in this repository uses the local `coverlet.console` tool against the built test assembly and must target the built `ManagedCode.MCPGateway.Tests` TUnit application directly; routing coverage through `dotnet test --solution ...` under `Microsoft.Testing.Platform` reports zero hits in this repo.
- Every behavior change must include or update tests in `tests/ManagedCode.MCPGateway.Tests/`.
- Treat parity work against the official `modelcontextprotocol/csharp-sdk` as incomplete until every newly exposed MCP capability is covered by automated tests, because unsupported or unverified protocol surface is explicitly rejected in this repository.
- Do not stop a `modelcontextprotocol/csharp-sdk` parity task at the stable subset when the user asked for full parity; either ship the remaining SDK-backed capabilities with tests or report a concrete technical blocker, because silent scope cuts are treated as missed delivery here.
- When adding or expanding MCP resource support, cover direct resources, templated resources, exported downstream resource URIs, and end-to-end read behavior with integration tests instead of narrow smoke checks.
- When coverage is reviewed or increased in this repository, prioritize stronger integration coverage for complex cross-source and protocol-heavy flows instead of adding shallow low-signal tests, because the user explicitly values hardening the difficult paths over inflating test count.
- When raising coverage in this repository, target at least mid-80s coverage for the important production files you touch or audit, because the user explicitly asked for 85-90 style coverage across the real code rather than a good-looking aggregate only.
- Add tests only when they close a meaningful behavior or regression gap; avoid low-signal tests that only increase count without improving confidence.
- Keep tests focused on real gateway behavior:
  - local tool indexing and invocation
  - MCP tool indexing and invocation
  - vector search behavior
  - Markdown-LD graph search and vector-to-graph fallback behavior
- Treat graph search as the default behavior under test; vector search tests are valid only when the test explicitly enables embeddings/vector strategy and should not redefine the default retrieval story away from graph/SPARQL.
- For MCP protocol coverage, prefer end-to-end integration tests that use the official `ModelContextProtocol` C# SDK on both client and server paths instead of test doubles when the behavior depends on real protocol contracts, because MCP regressions often hide outside mocked flows.
- When changing MCP discovery, routing, search, or invocation behavior, add multi-step integration cases with realistic tool metadata, side-effect boundaries, and client-driven execution so retrieval and invocation are verified under production-like conditions.
- Keep embedding-based search covered with deterministic local tests by using a fake or test-only embedding generator.
- Keep request context behavior covered when search or invocation consumes contextual inputs.
- Do not remove tests to get green builds.
- Keep `global.json` configured for `Microsoft.Testing.Platform` when TUnit is used.
- At the end of implementation work, run code-size and quality verification with `cloc`, `roslynator`, and the repository's strict .NET build/test checks, then fix actionable findings so oversized files and quality drift do not accumulate.
- `CSharpier` is installed as an opt-in local tool for focused checks and is not the default formatter or a default fast-path CI gate in this repository. `Stryker.NET` remains optional and is not assumed to be preinstalled in the local tool manifest.
- Run verification in this order:
  - tool-restore
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
- Avoid local wrapper/helper functions that only forward a result after side-effect bookkeeping when a single explicit result variable keeps the API flow clearer, because hidden return paths make the public behavior harder to audit.
- Keep MCP-facing and public API code paths direct; do not introduce local wrapper layers, proxy-style local helpers, or nested adapter functions when straightforward API-shaped control flow is enough, because extra wrapping obscures the real server behavior.
- During release stabilization, do not leave broad catch/default fallback paths in protocol, transport, schema, or task-result flows just because they appear harmless; replace them with explicit state checks, typed failure handling, and regression tests so quality issues are fixed instead of hidden.
- Prefer non-blocking coordination over coarse locking when practical; use concurrent collections, atomic state, and single-flight patterns instead of `lock`-heavy designs, because blocking synchronization has already proven to obscure concurrency behavior in this package.
- For async lifecycle races, prefer explicit owner/attempt reference tokens or appropriate built-in .NET cancellation/disposal primitives over numeric generation counters, because object identity makes cleanup ownership auditable and avoids brittle version-token logic.
- Never use sync-over-async bridges such as `.GetAwaiter().GetResult()`, `.Result`, or `.Wait()` in production code, tests, benchmarks, or setup/cleanup paths; keep async flows async end-to-end so benchmarks and tests do not hide deadlocks, scheduler blocking, or distorted performance measurements.
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
- Never leave stable graph/search scoring weights, thresholds, multipliers, limits, or other durable numeric tuning values inline in runtime code; extract named constants so ranking behavior is auditable and tunable from one place.
- Use the correct contextual logger type for each service; internal collaborators must log with their own type category instead of reusing a parent facade logger, because wrong logger categories make diagnostics misleading.
- Keep transport-specific logic inside the gateway and source registration abstractions, not scattered across the codebase.
- Keep the package dependency surface small and justified.
- Use the shipped NuGet dependency for `ManagedCode.MarkdownLd.Kb` in this repository; do not switch the gateway project to a local `ProjectReference`, because this package is expected to build against the published package surface.
- Prefer direct generic DI registrations such as `services.TryAddSingleton<IService, Implementation>()` over lambda alias registrations when wiring package services, because the lambda style has already been called out as unreadable and error-prone in this repository.
- Keep runtime services DI-native from their public/internal constructors; types such as `McpGatewayRegistry` must be creatable through `IOptions<McpGatewayOptions>` and other DI-managed dependencies rather than ad-hoc state-only constructors, because the package design requires services to live fully inside the container.
- When emitting package identity to external protocols such as MCP client info, never hardcode a fake version string; use the actual assembly/build version so runtime metadata stays aligned with the package being shipped.
- For search-quality improvements, prefer mathematical, statistical, or graph-ranking changes over hardcoded phrase lists or ad-hoc query text hacks, because the user explicitly wants token-distance search to improve through general scoring behavior rather than manual exceptions.
- Never clamp weak multilingual/noisy search matches to perfect confidence through boost stacking or score saturation, because this produces obviously wrong `Score = 1` results and makes relevance debugging impossible.
- Do not keep a separate local tokenizer search path when `ManagedCode.MarkdownLd.Kb` already provides token-based graph search; route tokenizer-backed retrieval through Markdown-LD so the package does not carry duplicate ranking implementations.
- Prefer framework-provided in-memory caching primitives such as `IMemoryCache` over custom process-local storage implementations when they cover the lifecycle and lookup needs, because self-rolled memory stores age poorly and make scaling/concurrency behavior harder to trust.
- Cache reusable search, graph, embedding, and normalization artifacts instead of recreating expensive objects on every request; prefer `IMemoryCache` for process-local reuse and keep extensibility behind interfaces so hosts can swap in durable or distributed implementations.
- Keep caching dependencies explicit and optional; do not make the core gateway path require `IMemoryCache` when a pluggable or no-op cache boundary can preserve a smaller dependency surface, because the user does not want avoidable cache dependencies forced into the base package.
- Keep index-building and warmup control explicit and customizable; expose understandable extension points so consumers can choose how and when the catalog or graph index is built instead of being forced into one hidden lifecycle.
- Do not add speculative public configuration options unless production runtime code consumes them and tests/docs prove the behavior; remove dead public options instead of expanding the package API surface.
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
- Vertical-slice structure with feature and subfeature isolation
- Reusable library design over app-specific glue
- Search + execute flows covered by automated tests
- Clean root packaging and CI setup
- Direct fixes over preserving legacy compatibility paths when cleanup or review-driven corrections are requested
- Framework-provided caching primitives over self-rolled in-memory stores when the package only needs process-local cache semantics
- Removing replaced code paths completely instead of keeping legacy mentions or compatibility leftovers
- Concise `McpGateway` public registration/init API names without leftover `ManagedCode` branding
- Straight-line public API control flow over local wrapper helpers that only add bookkeeping around returns
- Clean MCP API-shaped flows without extra local wrapper layers
- NuGet package dependencies over local project references for `ManagedCode.MarkdownLd.Kb`
- Production-ready feature implementations with real runtime behavior and test coverage instead of temporary or placeholder execution paths
- Developer-controlled index lifecycle and explicit cache-strategy selection over hardwired hidden defaults
- Full hybrid search that uses upstream graph/schema/federation capabilities end-to-end when those capabilities are available
- Clear API boundaries between MCP gateway operations and graph/search/index operations
- Real integration-style graph/SPARQL search coverage over mock-only search tests
- Built-in tools that expose graph schema/profile and index-build state for agent-visible diagnostics
- Benchmark-driven performance work with allocation statistics for search, index-build, and meta-tool hot paths

### Dislikes

- Agentic Framework dependency creep in this repository
- App-specific logic leaking into the shared gateway package
- Duplicate metadata and versions across multiple files
- Shipping behavior without tests
- Self-rolled in-memory storage when standard .NET caching abstractions already fit the scenario
- Legacy/obsolete compatibility leftovers after a replacement is accepted
- `ManagedCode`-prefixed public DI/setup API names that should have been cleaned up
- Mixing graph/search/index-specific operations into the MCP-facing gateway facade when a narrower search/index surface is the better boundary
- Old mock-only search tests that do not prove the real graph-first runtime behavior
- Speculative micro-optimizations that make the code harder to read without BenchmarkDotNet evidence
- Smoke-only benchmark CI that skips the real BenchmarkDotNet benchmark suite
