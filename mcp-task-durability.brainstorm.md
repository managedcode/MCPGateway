# MCP task access durability

The official SDK 2.3.0 task store requires an acknowledged task to remain accessible across processes/nodes. The current gateway wraps caller-owned durable stores with a process-local authorization dictionary and unconditionally drops records at its own 30-minute deadline. This makes durable host storage insufficient and can strand accepted long work.

Selected direction: bind the trusted task owner and source asset/hash atomically when the host creates the task, and read that binding from the same task store for every access check. Keep SDK protocol models, handlers and task execution. An explicit gateway task-store contract owns this gateway authorization metadata; it does not add a database or runtime dependency. Remove the parallel process-local owner registry and its separate TTL/capacity policy.

For the library's bounded development store, use the existing Microsoft.Extensions.Caching.Memory dependency, retain active/input-required tasks through their lifetime, and apply configurable result retention after a terminal outcome. Continue enforcing the configured maximum task count. Durable/distributed stores remain host-owned.

Prostir implements the access binding in its existing Orleans task grain and recovers Worker/Flow terminal observations through their existing exact owners. Never replay an opaque accepted tool invocation or introduce a new grain/store.

Out of scope: generic distributed tool executors, new infrastructure, automatic effect replay, unrelated SDK parity or source refactors. Preserve the existing unrelated local tool-manifest edit.
