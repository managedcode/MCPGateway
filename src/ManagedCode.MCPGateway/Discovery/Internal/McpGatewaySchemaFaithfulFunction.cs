using System.Text.Json;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.AI;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewaySchemaFaithfulFunction(IMcpGateway gateway, McpGatewayToolDescriptor descriptor) : AIFunction
{
    public override string Name => descriptor.ToolId;
    public override string Description => descriptor.Description;
    private static readonly JsonElement OutcomeSchema = AIJsonUtilities.CreateJsonSchema(typeof(McpGatewayInvokeResult),
        serializerOptions: McpGatewayJsonSerializer.Options);
    public override JsonElement JsonSchema => descriptor.InputSchema;
    public override JsonElement? ReturnJsonSchema => OutcomeSchema;
    public override JsonSerializerOptions JsonSerializerOptions => McpGatewayJsonSerializer.Options;
    private const string ReadOnlyHintProperty = "ReadOnly";
    private const string IdempotentHintProperty = "Idempotent";
    private const string DestructiveHintProperty = "Destructive";
    private const string OpenWorldHintProperty = "OpenWorld";
    public override IReadOnlyDictionary<string, object?> AdditionalProperties { get; } = CreateProperties(descriptor);
    private static IReadOnlyDictionary<string, object?> CreateProperties(McpGatewayToolDescriptor tool)
    {
        var properties = new Dictionary<string, object?>
        {
            [McpGatewayToolSet.DiscoveredToolIdPropertyName] = tool.ToolId,
            [McpGatewayToolSet.DiscoveredToolSourceIdPropertyName] = tool.SourceId,
        };
        if (tool.IsReadOnly is { } readOnly)
        { properties.Add(ReadOnlyHintProperty, readOnly); }
        if (tool.IsIdempotent is { } idempotent)
        { properties.Add(IdempotentHintProperty, idempotent); }
        if (tool.IsDestructive is { } destructive)
        { properties.Add(DestructiveHintProperty, destructive); }
        if (tool.IsOpenWorld is { } openWorld)
        { properties.Add(OpenWorldHintProperty, openWorld); }
        return properties;
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var result = await gateway.InvokeAsync(new McpGatewayInvokeRequest(ToolId: descriptor.ToolId, Arguments: arguments), cancellationToken);
        return McpGatewayJsonSerializer.TrySerializeToElement(result)
            ?? throw new JsonException("The gateway invocation outcome could not be serialized.");
    }
}
