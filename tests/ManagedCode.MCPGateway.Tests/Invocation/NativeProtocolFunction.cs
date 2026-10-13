using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Tests;

internal sealed class NativeProtocolFunction : AIFunction
{
    internal const string ToolName = "native_protocol_result";
    private const string DescriptionText = "Return complete native protocol results.";
    private static readonly string[] RequiredFields = ["result", "requestId"];
    private readonly Queue<CallToolResult> replies;

    internal NativeProtocolFunction(IEnumerable<CallToolResult> replies) => this.replies = new(replies);
    public override string Name => ToolName;
    public override string Description => DescriptionText;
    public override JsonElement JsonSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new { },
        additionalProperties = false
    });
    public override JsonElement? ReturnJsonSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            result = new { type = "integer" },
            requestId = new { type = "string" }
        },
        required = RequiredFields,
        additionalProperties = false
    });

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<object?>(replies.Dequeue());
    }
}
