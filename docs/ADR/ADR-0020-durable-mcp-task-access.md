# ADR-0020: Durable MCP task access and terminal retention

## Status

Accepted. Owner implementation, 451 tests, formatting, analyzers, Roslynator, vulnerability audit and the 92.46% line-coverage gate passed. Publication and consumer runtime validation remain pending.

## Context

SDK 2.3.0 requires task creation to be visible to subsequent polls on another process or node. The previous gateway adapter tracked authorization only in a ConcurrentDictionary and expired that tracking 30 minutes after creation independently of the selected task store. A durable host task could therefore become inaccessible after a gateway restart or a tracking sweep, even when its protocol TTL was unlimited.

## Decision

Keep the official SDK task extension, handlers and protocol models. Introduce IMcpGatewayTaskStore as the host persistence boundary: CreateTaskAsync receives a trusted McpGatewayTaskOwner and must atomically persist it with McpTaskInfo before acknowledging creation. The binding contains the subject, tenant, authorization partition, policy revision and optional source asset/definition hash. These are server-derived values and never caller/model parameters.

The SDK-facing adapter reads persisted ownership for each get, update and cancellation. It retains current authentication, access-policy and managed-publication checks. No process-local dictionary or additional expiry can overrule the selected host store. Unknown ownership fails closed; it is never inferred from a task handle or reconstructed as authorization for historical unbound tasks.

The bounded default uses the already referenced Microsoft.Extensions.Caching.Memory package. Working and input-required entries have no execution expiry. A first terminal outcome fixes the result, clears pending input and sets expiry to completion time plus CompletedTaskRetention; native protocol TTL remains expressed from task creation and can change over its lifetime as the specification permits. Capacity rejects new task creation rather than evicting active or retained results. The registered host TimeProvider supplies task timestamps and the native cache clock.

A durable/distributed host implements storage, capacity, retention and cancellation/input delivery in its existing task owner. This library does not create a database, journal, distributed executor or replay arbitrary accepted tool effects. The default development store remains intentionally process-local; production durability comes from the host store, including its authorization data.

## Verification

Real official SDK client/server cases recover successful and error results using a new host, DI container and freshly opened file store. They prove subject/policy isolation. A separate cross-instance case cancels persisted active work with tenant checks and proves late original completion cannot overwrite cancellation. A host-controlled clock verifies that active work outlives result retention, capacity remains bounded and only terminal expiry releases the slot. Existing SDK Tasks protocol/input/cancellation cases remain required.

Prostir must implement the binding in its existing Orleans task grain, recover Worker/Flow terminal observation through their existing exact owners, verify restart journeys and consume the published repair package. No new grain or persistence service is required.

## Delivery

Batch all confirmed task durability defects in one canonical patch release on SDK line 2.3. Verify owner checks, GitHub publication and actual NuGet availability, then update Prostir once and remove its temporary limitation note after the recovery acceptance passes.
