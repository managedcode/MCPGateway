# ADR-0011: Gateway-Owned Prompt Composition And List-Change Forwarding

## Status

Accepted

## Context

`ManagedCode.MCPGateway` already aggregated upstream MCP prompts and re-exported them through `IMcpGatewayPromptCatalog` and `WithMcpGatewayCatalog()`. That was enough for pass-through prompt listing and retrieval, but it still left two product gaps:

- the gateway could not define its own prompts that combine, modify, or overlay multiple upstream prompts
- downstream MCP clients could not receive `notifications/prompts/list_changed`, even though the underlying `ModelContextProtocol` C# SDK exposes `PromptsCapability.ListChanged`

These gaps mattered because the gateway is explicitly built on top of the official `modelcontextprotocol/csharp-sdk` and must stay source-aware when several upstream MCP servers expose similarly named prompts. Implicitly merging prompts by name would be ambiguous and would hide which upstream prompt actually produced which instruction set.

## Decision

The gateway will support explicit prompt composition through gateway-owned prompts, and it will forward prompt list-change signals to downstream MCP clients.

- local prompt registration is added to `McpGatewayOptions` and `IMcpGatewayRegistry` through `AddPrompt(...)` and `AddPrompts(...)`
- gateway-owned prompts are first-class members of `IMcpGatewayPromptCatalog`
- custom prompt renderers can fetch other prompts by `SourceId + PromptName` through `McpGatewayPromptRenderContext`
- prompt composition is explicit; prompts with the same upstream name are not merged implicitly
- gateway-owned prompts can expose prompt argument completions through `completion/complete`
- downstream `WithMcpGatewayCatalog()` export advertises `PromptsCapability.ListChanged`
- downstream MCP clients receive `notifications/prompts/list_changed` when:
  - gateway-owned prompts are added or the prompt-bearing source set is reconfigured
  - an upstream MCP source emits `notifications/prompts/list_changed`

## Consequences

- consumers can create one higher-level gateway prompt that combines several upstream prompt fragments into one curated prompt flow
- consumers can overlay extra instructions on top of upstream prompts without mutating the original upstream prompt definitions
- downstream prompt names remain source-qualified and unambiguous
- downstream MCP clients can refresh prompt pickers when the upstream or gateway-owned prompt catalog changes
- prompt composition stays inside the prompts slice instead of leaking transport-specific prompt logic into the core gateway orchestration path
- the existing shared source-registration implementation remains temporarily above the repository file-size target while prompt, resource, completion, and task proxy logic still live in one internal file; the follow-up path is to split local source registration, client source registration, and notification subscription logic into dedicated files without changing the public API

## Requirements

- prompt composition MUST be source-aware and MUST require explicit registration; same-named prompts from different upstream sources MUST NOT be merged automatically
- gateway-owned prompts MUST be listable and retrievable through `IMcpGatewayPromptCatalog`
- gateway-owned prompt argument completion MUST flow through downstream MCP `completion/complete`
- `WithMcpGatewayCatalog()` MUST advertise `PromptsCapability.ListChanged`
- upstream `notifications/prompts/list_changed` MUST be forwarded to downstream MCP clients
- every newly exposed prompt behavior MUST be covered by automated tests that use the official `ModelContextProtocol` C# SDK where protocol behavior matters
