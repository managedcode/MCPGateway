# ADR-0014: Current MCP SDK 2 Protocol And Extensions

## Status

Accepted

## Context

`ManagedCode.MCPGateway` builds directly on the official MCP C# SDK `2.0.0`. Its package surface needs one unambiguous protocol contract across gateway-created upstream clients, caller-provided clients, and downstream server export.

The selected protocol revision provides:

- discovery through `server/discover`
- per-request protocol, client, and capability metadata
- cache-aware result metadata
- long-lived notification selection through `subscriptions/listen`
- Tasks through `ModelContextProtocol.Extensions.Tasks`
- MCP Apps through `ModelContextProtocol.Extensions.Apps`
- arbitrary JSON Schema 2020-12 output shapes

`ManagedCode.MarkdownLd.Kb` `0.2.8` also improves graph deduplication and confidence bounds used by generated and file-backed gateway catalogs.

## Decision

### Protocol And Transports

- Pin every gateway-created `McpClientOptions` and exported `McpServerOptions` instance to `2026-07-28`.
- Validate caller-provided clients after connection and reject any negotiated protocol other than `2026-07-28` with `UnsupportedProtocolVersionException`.
- Use only the official SDK Streamable HTTP client transport for HTTP sources.
- Keep HTTP source configuration limited to endpoint, display name, headers, connection timeout, and OAuth.
- Enforce `HttpServerTransportOptions.Stateless = true` on gateway server export.
- Use the official SDK stdio transport and expose its environment, shutdown, and stderr options through `McpGatewayStdioServerOptions`.
- Preserve arbitrary upstream output schemas, including boolean, primitive, array, and object schemas.

### Subscriptions

- Coordinate `subscriptions/listen` before the SDK sends its acknowledgement.
- Model gateway source listeners as `ListenForPromptListChangesAsync(...)` and `ListenForResourceUpdatesAsync(...)` lifetimes rather than retaining the removed resource subscribe/unsubscribe RPC shape.
- Create only the upstream prompt-list and resource subscriptions granted to that listener.
- Tag forwarded notifications with the listen request id in `_meta/io.modelcontextprotocol/subscriptionId`.
- Gate early callbacks until acknowledgement delivery, retain at most the latest pending notification per prompt or resource key, and release every upstream subscription when the listener is cancelled or ends.
- Return no notification grants for stateless HTTP listeners.

### Tasks

- Register `ModelContextProtocol.Extensions.Tasks` `2.0.0` through `WithTasks(...)`.
- Support `tasks/get`, `tasks/update`, and `tasks/cancel` through the SDK extension contracts.
- Execute downstream task requests through the SDK alternate-result filter and `IMcpTaskStore`.
- Provide a bounded default store with configurable TTL, poll interval, total task limit, and unchanged-poll limit.
- Accept a caller-owned `IMcpTaskStore` when durable or distributed task state is required.
- Retry an upstream tool through the SDK polling helper only when the peer explicitly requires and advertises Tasks.

### MCP Apps

- Advertise `io.modelcontextprotocol/ui` on downstream export.
- Advertise support for `text/html;profile=mcp-app` from gateway-created upstream clients.
- Preserve upstream tool and resource metadata and MIME types.
- Rewrite `_meta.ui.resourceUri` into the same source-qualified gateway URI space used by resource reads.

### Markdown-LD

- Use `ManagedCode.MarkdownLd.Kb` `0.2.8`.
- Keep schema-aware SPARQL as the primary graph retrieval path in hybrid mode.
- Verify that repeated catalog documents do not increase graph node or edge counts and that confidence remains bounded.

## Consequences

- The gateway has one current protocol and no gateway-owned compatibility branches.
- HTTP sources and exports have one transport behavior.
- Non-current peers fail immediately instead of silently selecting a different contract.
- Listener and task state remains bounded and has deterministic cleanup.
- Tasks and Apps use official SDK extension packages; experimental SDK annotations remain isolated to the integration boundary.
- Caller-provided upstream clients remain responsible for their own MRTR input handlers because the public SDK resolves input-required results before returning from `McpClient`.

## Invariants

- The gateway MUST use official SDK transports, protocol models, Tasks, and Apps packages.
- Every MCP connection created or exported by the gateway MUST use `2026-07-28`.
- The gateway MUST NOT implement alternative JSON-RPC protocol or transport paths.
- Every listener MUST have bounded pending delivery state and deterministic cleanup.
- Forwarded resource and App UI URIs MUST remain source-qualified gateway URIs.
- A caller-provided `IMcpTaskStore` MUST be the instance used by the Tasks extension.
- Streamable HTTP MUST remain stateless.
- Boolean and other non-object output schemas MUST survive catalog and downstream export mapping.

## Verification

- Gateway client and server options assert the exact protocol revision.
- A real non-current SDK client is rejected before source use.
- Prompt and resource subscriptions exercise acknowledgement ordering, notification tagging, cancellation, and cleanup through real SDK client/server connections.
- Tasks cover upstream retry, local and upstream execution, polling, cancellation, failures, bounded storage, and caller-provided stores.
- MCP Apps cover extension capabilities and App UI resource URI rewriting.
- Repeated generated Markdown-LD documents cover graph and JSON-LD deduplication.
- Stdio transport tests cover environment inheritance control, explicit environment forwarding, shutdown timeout, and stderr callback mapping.
- A real SDK client/server integration preserves primitive structured results as raw JSON values.
- Restore, build, analyzer, test, formatting, dependency-vulnerability, and code-size gates remain required.

## References

- [MCP C# SDK v2.0.0](https://github.com/modelcontextprotocol/csharp-sdk/releases/tag/v2.0.0)
- [SDK Tasks documentation](https://csharp.sdk.modelcontextprotocol.io/v2/concepts/tasks/tasks.html)
- [SDK MCP Apps documentation](https://csharp.sdk.modelcontextprotocol.io/v2/concepts/apps/apps.html)
- [ManagedCode.MarkdownLd.Kb v0.2.8](https://github.com/managedcode/markdown-ld-kb/releases/tag/v0.2.8)
