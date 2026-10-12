using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway;

internal static class McpGatewayResultDelivery
{
    private const string OutputSchemaInvalidCode = "output_schema_invalid";
    private const string OutputSchemaInvalidMessage = "The approved result does not satisfy the tool output schema.";
    private const string ResultRejectedCode = "result_rejected";
    private const string ResultRejectedMessage = "The result could not be delivered under the configured output policy.";
    private const string OversizedCode = "result_too_large";
    private const string OversizedMessage = "The approved result exceeds the delivery byte budget.";

    public static async Task<McpGatewayInvokeResult> DeliverAsync(IServiceProvider services, McpGatewayToolDescriptor tool,
        McpGatewayInvokeRequest request, McpGatewayInvokeResult result, McpGatewayExecutionContext context, McpGatewaySchemaValidator validator,
        bool managedCatalog, int maximumDeliveryBytes, CancellationToken cancellationToken)
    {
        services = McpGatewayTrustedContext.OperationServices(services);
        if (!result.IsSuccess)
        { return result; }
        if (!(await McpGatewayAccessBoundary.AuthorizeAsync(services, context,
            new(McpGatewayOperation.DeliverResult, tool.ToolId, tool.SourceId)
            { RequiredPermissions = tool.RequiredPermissions, DefinitionHash = tool.DefinitionHash }, cancellationToken)).IsAllowed)
        {
            return Reject(result, ResultRejectedCode, ResultRejectedMessage);
        }
        if (managedCatalog)
        {
            var catalog = await services.GetRequiredService<IMcpGatewayCatalogDefinitionStore>().ReadAsync(cancellationToken);
            if (catalog.RevokedToolIds.Contains(tool.ToolId) || !catalog.Definitions.Any(definition =>
                definition.ToolId.Equals(tool.ToolId, StringComparison.OrdinalIgnoreCase) && definition.DefinitionHash == tool.DefinitionHash
                && definition.Lifecycle == McpGatewayToolLifecycle.Published))
            {
                return Reject(result, ResultRejectedCode, ResultRejectedMessage);
            }
        }
        var policy = services.GetService<IMcpGatewayResultPolicy>();
        var response = request.ResponseOptions;
        var output = McpGatewayJsonSerializer.TrySerializeToElement(result.Output);
        if (result.Output is not null && output is null)
        {
            return Reject(result, ResultRejectedCode, ResultRejectedMessage);
        }
        if (McpGatewayInvocationTelemetry.SourceSizeEnabled)
        { McpGatewayInvocationTelemetry.SourcePayload(Measure(result)); }
        if (policy is not null)
        {
            var approved = await policy.ApproveAsync(context, tool, output, result.McpResult, cancellationToken);
            if (approved.IsRejected)
            {
                return Reject(result, ResultRejectedCode, ResultRejectedMessage);
            }
            output = approved.Output;
            result = result with { Output = output, McpResult = output is not null ? CreateProtocol(output, result.Outcome == McpGatewayInvocationOutcome.ToolError) : approved.ProtocolResult };
        }
        if (result.Outcome == McpGatewayInvocationOutcome.Succeeded && !validator.ValidateOutput(output))
        {
            return Reject(result, OutputSchemaInvalidCode, OutputSchemaInvalidMessage);
        }
        if (McpGatewayInvocationTelemetry.ApprovedSizeEnabled)
        { McpGatewayInvocationTelemetry.ApprovedPayload(Measure(result)); }
        if (response is { SelectPaths.Count: > 0 })
        {
            var source = McpGatewayJsonSerializer.TrySerializeToNode(output);
            var projected = McpGatewayJsonProjection.Project(source, response.SelectPaths);
            output = McpGatewayJsonSerializer.TrySerializeToElement(projected);
            result = result with { Output = output, McpResult = null };
            McpGatewayInvocationTelemetry.Projection();
        }
        // A policy returning only approved JSON replaces every raw protocol representation.
        if (result.McpResult is null && (policy is not null || response?.SelectPaths.Count > 0))
        {
            result = result with { McpResult = CreateProtocol(output, result.Outcome == McpGatewayInvocationOutcome.ToolError) };
        }
        var bytes = Measure(result);
        if (response is not null)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(response.MaxBytes, 1);
            if (bytes > response.MaxBytes)
            {
                if (response.StoreOversizedResult && services.GetService<IMcpGatewayResultStore>() is { } store)
                {
                    var approved = McpGatewayJsonSerializer.TrySerializeToElement(result.McpResult is null ? result.Output : result.McpResult);
                    var stored = approved is not { } value ? null : managedCatalog
                        ? await store.StoreAsync(context, value, tool, maximumDeliveryBytes, cancellationToken)
                        : await store.StoreAsync(context, value, tool, cancellationToken);
                    if (stored is not null)
                    {
                        if (managedCatalog && (!stored.RequiresPublication || stored.ToolId != tool.ToolId
                            || stored.DefinitionHash != tool.DefinitionHash || stored.SourceId != tool.SourceId
                            || stored.MaximumDeliveryBytes != maximumDeliveryBytes))
                        {
                            await store.RemoveAsync(context, stored.Handle, cancellationToken);
                            return Reject(result, ResultRejectedCode, ResultRejectedMessage);
                        }
                        McpGatewayInvocationTelemetry.StoredResult();
                        return result with { Output = null, McpResult = null, ResultHandle = stored.Handle, DeliveredBytes = 0 };
                    }
                }
                return Reject(result, OversizedCode, OversizedMessage);
            }
        }
        return result with { DeliveredBytes = bytes };
    }

    private static int Measure(McpGatewayInvokeResult result)
    {
        var outward = result.McpResult is null
            ? McpGatewayJsonSerializer.TrySerializeToElement(result.Output)
            : McpGatewayJsonSerializer.TrySerializeToElement(result.McpResult);
        return outward is null ? 0 : Encoding.UTF8.GetByteCount(outward.Value.GetRawText());
    }

    internal static CallToolResult CreateProtocol(JsonElement? output, bool isError) => new()
    {
        IsError = isError,
        StructuredContent = output,
        Content = output is { } value ? [new TextContentBlock { Text = value.GetRawText() }] : [],
    };

    private static McpGatewayInvokeResult Reject(McpGatewayInvokeResult result, string code, string message) =>
        result with
        {
            IsSuccess = false,
            Outcome = McpGatewayInvocationOutcome.ResultRejected,
            ErrorCode = code,
            Error = message,
            Output = null,
            McpResult = null,
            ResultHandle = null
        };
}
