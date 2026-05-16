# ADR-0010: MCP Export Task Surface Parity

## Status

Accepted

## Context

After `ADR-0009`, `ManagedCode.MCPGateway` exported aggregated MCP tools, prompts, resources, completions, resource subscriptions, and logging-level negotiation through `WithMcpGatewayCatalog()`. The remaining gap against the official `modelcontextprotocol/csharp-sdk` surface was task-backed MCP tool execution:

- exported tools did not describe `execution.taskSupport`
- downstream `tools/call` requests with `task` metadata were not supported
- downstream MCP clients could not use `tasks/list`, `tasks/get`, `tasks/result`, or `tasks/cancel` against the gateway export
- forwarded `notifications/tasks/status` were missing

This blocked parity with SDK-backed task flows, especially for upstream MCP tools that explicitly require task augmentation.

## Decision

`WithMcpGatewayCatalog()` will export the MCP tasks surface for tool execution.

- exported tool metadata will include `execution.taskSupport`
- upstream MCP tools will preserve their declared task support when the gateway re-exports them
- local gateway tools will be exported as optional task-capable tools
- task-augmented downstream `tools/call` requests will create gateway-managed tasks
- when an upstream MCP source already supports task-backed tool calls, the gateway will proxy that upstream task flow instead of re-executing the tool synchronously
- gateway-managed tasks will be stored through a dedicated downstream MCP task store wired into `McpServerOptions.TaskStore`
- the default gateway-owned task store will use the official SDK `InMemoryMcpTaskStore` with bounded TTL, cleanup, global task limit, and per-session task limit options instead of unbounded process memory
- active task bindings owned by a downstream Streamable HTTP session will be cancelled and released when the official SDK HTTP session lifecycle ends
- the gateway export will advertise MCP `tasks` capabilities for tool requests, task listing, and task cancellation
- downstream MCP clients will receive forwarded `notifications/tasks/status`

## Consequences

- downstream MCP clients can use `tools/call` with task metadata plus `tasks/list`, `tasks/get`, `tasks/result`, and `tasks/cancel` against the aggregated gateway export
- upstream MCP tools that require task augmentation stay invokable through the gateway instead of failing on a synchronous proxy path
- local gateway tools gain optional task-backed execution without changing the core `IMcpGateway` invocation API
- task result handling stays source-aware: upstream task-backed tools are resolved through the owning upstream MCP client, while local tasks are completed through the gateway-owned task store
- process-local task retention is bounded by default and can be tuned through `McpGatewayOptions.McpTaskStore`; hosts that need durable or distributed task history should replace `McpServerOptions.TaskStore`
- task binding cleanup is tied to explicit task completion/cancellation and to the official Streamable HTTP session lifecycle; the gateway does not introduce a separate custom MCP session store

## Requirements

- `WithMcpGatewayCatalog()` MUST advertise MCP task capabilities for tool requests, task listing, and task cancellation.
- Exported MCP tools MUST expose `execution.taskSupport`.
- Upstream MCP tools marked as `Required` for task support MUST not be downgraded to synchronous-only execution on the gateway export.
- Task-backed `tools/call` requests for local gateway tools MUST create real gateway-managed tasks instead of placeholder responses.
- The default gateway-owned task store MUST configure positive TTL, cleanup, and task-count limits and MUST dispose the SDK in-memory store with the MCPGateway task store.
- Session cleanup MUST cancel active local task bindings before waiting for their operations to finish.
- `notifications/tasks/status` MUST be forwarded for exported gateway tasks.
- Every newly exposed task capability MUST be covered by integration tests that use the official `ModelContextProtocol` C# SDK on both client and server paths.
