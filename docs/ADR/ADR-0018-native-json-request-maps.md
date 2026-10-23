# ADR-0018: Native JSON request maps

## Status

Accepted for the caller-authored JSON contract repair.

## Context

Arguments and contextual metadata originate as JSON in MCP and application callers. Object-valued request dictionaries exposed an arbitrary CLR graph even though schema validation and protocol delivery operate on JSON, requiring consumers to author untyped bags. This was also inconsistent with already-native MCP JSON arguments. Trusted execution identity is a separate server-owned boundary.

## Decision

Use `IReadOnlyDictionary<string, JsonElement>` for tool and prompt arguments, access-request arguments and search, route and invocation context. Invocation clones each element before validation; protocol argument copying also clones values. Keep the existing invocation key comparer, schema rules, explicit-argument precedence and authorization ordering. Case-colliding invocation keys are invalid arguments and cannot dispatch a tool. Preserve payload property casing, number tokens, explicit nulls and nested JSON unchanged.

Convert to the genuine SDK object-valued argument ABI only at the external SDK call or constructor, boxing each `JsonElement` without a recursive converter. Local SDK function input is normalized through the existing native System.Text.Json path. Do not introduce another serializer, converter, fallback overload or alternate wire protocol.

Keep opaque SDK properties and trusted `McpGatewayExecutionContext` unchanged. A caller-authored context field never grants server identity or access.

## Verification

Real HTTP MCP discovery/meta-tool invocation regressions commit nested JSON and independently read the committed outcome. They cover large integer and decimal precision, explicit null, arrays, property casing, explicit query precedence, invalid schema, case-colliding keys and a foreign authenticated caller attempting to forge identity in JSON context. Existing prompt, local/MCP invocation, search and proxy tests carry the same canonical contract. Execution results are recorded separately from this source design.
