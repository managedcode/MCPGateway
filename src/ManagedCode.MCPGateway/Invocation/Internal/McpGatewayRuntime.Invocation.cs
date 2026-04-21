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
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        var snapshot = await GetSnapshotAsync(cancellationToken);
        var resolution = ResolveInvocationTarget(snapshot, request);
        if (!resolution.IsSuccess || resolution.Entry is null)
        {
            return CreateInvocationFailure(
                request.ToolId ?? string.Empty,
                request.SourceId ?? string.Empty,
                request.ToolName ?? string.Empty,
                resolution.Error
            );
        }

        var entry = resolution.Entry;
        var arguments = BuildInvocationArguments(request, entry.Descriptor);

        try
        {
            return await InvokeResolvedToolAsync(entry, request, arguments, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, GatewayInvocationFailedLogMessage, entry.Descriptor.ToolId);
            return CreateInvocationFailure(
                entry.Descriptor.ToolId,
                entry.Descriptor.SourceId,
                entry.Descriptor.ToolName,
                ex.GetBaseException().Message
            );
        }
    }

    private static Dictionary<string, object?> BuildInvocationArguments(
        McpGatewayInvokeRequest request,
        McpGatewayToolDescriptor descriptor
    )
    {
        var arguments = request.Arguments is { Count: > 0 }
            ? new Dictionary<string, object?>(request.Arguments, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        if (
            !string.IsNullOrWhiteSpace(request.Query)
            && !arguments.ContainsKey(QueryArgumentName)
            && SupportsArgument(descriptor, QueryArgumentName)
        )
        {
            arguments[QueryArgumentName] = request.Query;
        }

        MapRequestArgument(arguments, descriptor, ContextArgumentName, request.Context);
        MapRequestArgument(
            arguments,
            descriptor,
            ContextSummaryArgumentName,
            request.ContextSummary
        );
        return arguments;
    }

    private static async Task<McpGatewayInvokeResult> InvokeResolvedToolAsync(
        ToolCatalogEntry entry,
        McpGatewayInvokeRequest request,
        Dictionary<string, object?> arguments,
        CancellationToken cancellationToken
    )
    {
        var resolvedMcpTool = entry.Tool as McpClientTool ?? entry.Tool.GetService<McpClientTool>();
        if (resolvedMcpTool is not null)
        {
            return await InvokeMcpToolAsync(
                entry,
                request,
                arguments,
                resolvedMcpTool,
                cancellationToken
            );
        }

        var function = entry.Tool as AIFunction ?? entry.Tool.GetService<AIFunction>();
        if (function is null)
        {
            return CreateInvocationFailure(
                entry.Descriptor.ToolId,
                entry.Descriptor.SourceId,
                entry.Descriptor.ToolName,
                string.Format(
                    CultureInfo.InvariantCulture,
                    ToolNotInvokableMessageFormat,
                    entry.Descriptor.ToolName
                )
            );
        }

        return await InvokeFunctionAsync(entry, arguments, function, cancellationToken);
    }

    private static async Task<McpGatewayInvokeResult> InvokeMcpToolAsync(
        ToolCatalogEntry entry,
        McpGatewayInvokeRequest request,
        Dictionary<string, object?> arguments,
        McpClientTool tool,
        CancellationToken cancellationToken
    )
    {
        var result = await AttachInvocationMeta(tool, request)
            .CallAsync(
                arguments,
                progress: null,
                options: new RequestOptions(),
                cancellationToken: cancellationToken
            );

        return new McpGatewayInvokeResult(
            true,
            entry.Descriptor.ToolId,
            entry.Descriptor.SourceId,
            entry.Descriptor.ToolName,
            ExtractMcpOutput(result)
        );
    }

    private static async Task<McpGatewayInvokeResult> InvokeFunctionAsync(
        ToolCatalogEntry entry,
        Dictionary<string, object?> arguments,
        AIFunction function,
        CancellationToken cancellationToken
    )
    {
        var resultValue = await function.InvokeAsync(
            new AIFunctionArguments(arguments, StringComparer.OrdinalIgnoreCase),
            cancellationToken
        );

        return new McpGatewayInvokeResult(
            true,
            entry.Descriptor.ToolId,
            entry.Descriptor.SourceId,
            entry.Descriptor.ToolName,
            NormalizeFunctionOutput(resultValue)
        );
    }

    private static McpGatewayInvokeResult CreateInvocationFailure(
        string toolId,
        string sourceId,
        string toolName,
        string? error
    ) => new(false, toolId, sourceId, toolName, Output: null, Error: error);

    private static void MapRequestArgument(
        IDictionary<string, object?> arguments,
        McpGatewayToolDescriptor descriptor,
        string argumentName,
        object? value
    )
    {
        if (
            value is null
            || arguments.ContainsKey(argumentName)
            || !SupportsArgument(descriptor, argumentName)
        )
        {
            return;
        }

        if (value is string text && string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        arguments[argumentName] = value;
    }

    private static bool SupportsArgument(McpGatewayToolDescriptor descriptor, string argumentName)
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
            if (
                !schemaDocument.RootElement.TryGetProperty(
                    InputSchemaPropertiesPropertyName,
                    out var properties
                )
                || properties.ValueKind != JsonValueKind.Object
            )
            {
                return false;
            }

            return properties
                .EnumerateObject()
                .Any(property =>
                    string.Equals(property.Name, argumentName, StringComparison.OrdinalIgnoreCase)
                );
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static McpClientTool AttachInvocationMeta(
        McpClientTool tool,
        McpGatewayInvokeRequest request
    )
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
            var contextNode = McpGatewayJsonSerializer.TrySerializeToNode(request.Context);
            if (contextNode is not null)
            {
                payload[ContextArgumentName] = contextNode;
            }
        }

        return payload.Count == 0 ? null : new JsonObject { [GatewayInvocationMetaKey] = payload };
    }
}
