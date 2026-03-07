using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;

namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime
{
    public async Task<McpGatewayInvokeResult> InvokeAsync(
        McpGatewayInvokeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var snapshot = await GetSnapshotAsync(cancellationToken);
        var resolution = ResolveInvocationTarget(snapshot, request);
        if (!resolution.IsSuccess || resolution.Entry is null)
        {
            return new McpGatewayInvokeResult(
                false,
                request.ToolId ?? string.Empty,
                request.SourceId ?? string.Empty,
                request.ToolName ?? string.Empty,
                Output: null,
                Error: resolution.Error);
        }

        var entry = resolution.Entry;
        var arguments = request.Arguments is { Count: > 0 }
            ? new Dictionary<string, object?>(request.Arguments, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(request.Query) &&
            !arguments.ContainsKey(QueryArgumentName) &&
            SupportsArgument(entry.Descriptor, QueryArgumentName))
        {
            arguments[QueryArgumentName] = request.Query;
        }

        MapRequestArgument(arguments, entry.Descriptor, ContextArgumentName, request.Context);
        MapRequestArgument(arguments, entry.Descriptor, ContextSummaryArgumentName, request.ContextSummary);

        try
        {
            var resolvedMcpTool = entry.Tool as McpClientTool ?? entry.Tool.GetService<McpClientTool>();
            if (resolvedMcpTool is not null)
            {
                var result = await AttachInvocationMeta(resolvedMcpTool, request).CallAsync(
                    arguments,
                    progress: null,
                    options: new RequestOptions(),
                    cancellationToken: cancellationToken);

                return new McpGatewayInvokeResult(
                    true,
                    entry.Descriptor.ToolId,
                    entry.Descriptor.SourceId,
                    entry.Descriptor.ToolName,
                    ExtractMcpOutput(result));
            }

            var function = entry.Tool as AIFunction ?? entry.Tool.GetService<AIFunction>();
            if (function is null)
            {
                return new McpGatewayInvokeResult(
                    false,
                    entry.Descriptor.ToolId,
                    entry.Descriptor.SourceId,
                    entry.Descriptor.ToolName,
                    Output: null,
                    Error: string.Format(CultureInfo.InvariantCulture, ToolNotInvokableMessageFormat, entry.Descriptor.ToolName));
            }

            var resultValue = await function.InvokeAsync(
                new AIFunctionArguments(arguments, StringComparer.OrdinalIgnoreCase),
                cancellationToken);
            return new McpGatewayInvokeResult(
                true,
                entry.Descriptor.ToolId,
                entry.Descriptor.SourceId,
                entry.Descriptor.ToolName,
                NormalizeFunctionOutput(resultValue));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, GatewayInvocationFailedLogMessage, entry.Descriptor.ToolId);
            return new McpGatewayInvokeResult(
                false,
                entry.Descriptor.ToolId,
                entry.Descriptor.SourceId,
                entry.Descriptor.ToolName,
                Output: null,
                Error: ex.GetBaseException().Message);
        }
    }

    private static void MapRequestArgument(
        IDictionary<string, object?> arguments,
        McpGatewayToolDescriptor descriptor,
        string argumentName,
        object? value)
    {
        if (value is null ||
            arguments.ContainsKey(argumentName) ||
            !SupportsArgument(descriptor, argumentName))
        {
            return;
        }

        if (value is string text && string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        arguments[argumentName] = value;
    }

    private static bool SupportsArgument(
        McpGatewayToolDescriptor descriptor,
        string argumentName)
    {
        if (descriptor.RequiredArguments.Contains(argumentName, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(descriptor.InputSchemaJson))
        {
            return false;
        }

        try
        {
            using var schemaDocument = JsonDocument.Parse(descriptor.InputSchemaJson);
            if (!schemaDocument.RootElement.TryGetProperty(InputSchemaPropertiesPropertyName, out var properties) ||
                properties.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            return properties
                .EnumerateObject()
                .Any(property => string.Equals(property.Name, argumentName, StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static McpClientTool AttachInvocationMeta(McpClientTool tool, McpGatewayInvokeRequest request)
    {
        var meta = BuildInvocationMeta(request);
        return meta is null ? tool : tool.WithMeta(meta);
    }

    private static JsonObject? BuildInvocationMeta(McpGatewayInvokeRequest request)
    {
        var payload = new JsonObject();
        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            payload[QueryArgumentName] = request.Query.Trim();
        }

        if (!string.IsNullOrWhiteSpace(request.ContextSummary))
        {
            payload[ContextSummaryArgumentName] = request.ContextSummary.Trim();
        }

        if (request.Context is { Count: > 0 })
        {
            var contextNode = JsonSerializer.SerializeToNode(request.Context);
            if (contextNode is not null)
            {
                payload[ContextArgumentName] = contextNode;
            }
        }

        return payload.Count == 0
            ? null
            : new JsonObject
            {
                [GatewayInvocationMetaKey] = payload
            };
    }

}
