using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayGovernanceMessageFilter(IServiceProvider services, McpGatewayExecutionScope scope)
{
    private const string ToolNameProperty = "name";
    private const string ArgumentsProperty = "arguments";
    private const string ToolIdProperty = "toolId";
    internal McpMessageFilter Create() => next => async (request, cancellationToken) =>
    {
        var operationServices = request.Services ?? services;
        var provider = operationServices.GetService<IMcpGatewayExecutionContextProvider>() ?? services.GetService<IMcpGatewayExecutionContextProvider>();
        var context = provider is null ? McpGatewayExecutionContext.TrustedLocal : await provider.GetCurrentAsync(cancellationToken);
        var asset = ResolveAsset(request.JsonRpcMessage);
        string? hash = null;
        if (asset is not null && operationServices.GetService<IMcpGatewayCatalogDefinitionStore>() is { } store)
        {
            var catalog = await store.ReadAsync(cancellationToken);
            hash = catalog.Definitions.FirstOrDefault(definition => definition.ToolId.Equals(asset, StringComparison.OrdinalIgnoreCase))?.DefinitionHash;
        }
        using var frame = scope.Push(context, operationServices, asset, hash);
        await next(request, cancellationToken);
    };

    private static string? ReadString(JsonObject value, string property) =>
        value.FirstOrDefault(pair => pair.Key.Equals(property, StringComparison.OrdinalIgnoreCase)).Value is JsonValue node
            && node.TryGetValue<string>(out var text) ? text : null;

    private static string? ResolveAsset(JsonRpcMessage message)
    {
        if (message is not JsonRpcRequest { Method: RequestMethods.ToolsCall, Params: JsonObject parameters })
        {
            return null;
        }
        var name = ReadString(parameters, ToolNameProperty);
        if (name?.Equals(McpGatewayToolSet.DefaultInvokeToolName, StringComparison.OrdinalIgnoreCase) is true)
        {
            return parameters.FirstOrDefault(pair => pair.Key.Equals(ArgumentsProperty, StringComparison.OrdinalIgnoreCase)).Value is JsonObject arguments
                ? ReadString(arguments, ToolIdProperty) : null;
        }
        return name;
    }
}
