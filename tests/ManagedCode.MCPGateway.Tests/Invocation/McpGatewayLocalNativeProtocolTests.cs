using System.Text.Json;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayLocalNativeProtocolTests
{
    private const string Source = "native";
    private const string OriginalJson = "{\"result\":7,\"requestId\":\"original\"}";
    private const string InvalidJson = "{\"result\":\"invalid\",\"requestId\":\"invalid\"}";
    private const string ErrorJson = "{\"error\":{\"code\":\"PermissionDenied\"},\"requestId\":\"denied\"}";
    private const string HealthyJson = "{\"result\":9,\"requestId\":\"healthy\"}";

    [TUnit.Core.Test]
    public async Task ActualLocalNativeResultsValidateStructuredContentRetainErrorAndRepair()
    {
        var original = Reply(OriginalJson, false);
        var invalid = Reply(InvalidJson, false);
        var denied = Reply(ErrorJson, true);
        var healthy = Reply(HealthyJson, false);
        var function = new NativeProtocolFunction([original, invalid, denied, healthy]);
        await using var services = GatewayTestServiceProviderFactory.Create(options =>
            options.AddTool(Source, function));
        var gateway = services.GetRequiredService<IMcpGateway>();
        await gateway.BuildIndexAsync();
        var request = new McpGatewayInvokeRequest(ToolId: NativeProtocolFunction.ToolName);
        var first = await gateway.InvokeAsync(request);
        await RequireAsync(first, original, McpGatewayInvocationOutcome.Succeeded);
        var refusal = await gateway.InvokeAsync(request);
        await Assert.That(refusal.IsSuccess).IsFalse();
        await Assert.That(refusal.Outcome).IsEqualTo(McpGatewayInvocationOutcome.ResultRejected);
        await Assert.That(refusal.Output).IsNull();
        await Assert.That(refusal.McpResult).IsNull();
        var error = await gateway.InvokeAsync(request);
        await RequireAsync(error, denied, McpGatewayInvocationOutcome.ToolError);
        await Assert.That(error.ErrorCode).IsNotNull();
        var repaired = await gateway.InvokeAsync(request);
        await RequireAsync(repaired, healthy, McpGatewayInvocationOutcome.Succeeded);
        await Assert.That(first.McpResult!.StructuredContent!.Value.GetRawText()).IsEqualTo(OriginalJson);
    }

    private static async Task RequireAsync(McpGatewayInvokeResult actual, CallToolResult expected,
        McpGatewayInvocationOutcome outcome)
    {
        await Assert.That(actual.IsSuccess).IsTrue();
        await Assert.That(actual.Outcome).IsEqualTo(outcome);
        await Assert.That(ReferenceEquals(actual.McpResult, expected)).IsTrue();
        await Assert.That(actual.Output).IsTypeOf<JsonElement>();
        await Assert.That(JsonElement.DeepEquals((JsonElement)actual.Output!,
            expected.StructuredContent!.Value)).IsTrue();
        await Assert.That(actual.DeliveredBytes > 0).IsTrue();
        await Assert.That(actual.McpResult!.IsError).IsEqualTo(expected.IsError);
        await Assert.That(actual.McpResult.Content.Single()).IsTypeOf<TextContentBlock>();
        await Assert.That(((TextContentBlock)actual.McpResult.Content.Single()).Text)
            .IsEqualTo(expected.StructuredContent.Value.GetRawText());
    }

    private static CallToolResult Reply(string json, bool error)
    {
        using var document = JsonDocument.Parse(json);
        return new()
        {
            IsError = error,
            StructuredContent = document.RootElement.Clone(),
            Content = [new TextContentBlock { Text = json }]
        };
    }
}
