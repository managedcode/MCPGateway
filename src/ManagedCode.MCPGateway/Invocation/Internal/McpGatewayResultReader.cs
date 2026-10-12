using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayResultReader(IServiceProvider services,
    IOptions<McpGatewayOptions> options) : IMcpGatewayResultReader
{
    public async Task<McpGatewayStoredResult?> ReadAsync(string handle, McpGatewayResponseOptions? response = null,
        CancellationToken cancellationToken = default)
    {
        var context = await McpGatewayTrustedContext.ResolveAsync(services, cancellationToken);
        var stored = await ReadAuthorizedAsync(context, handle, cancellationToken);
        if (stored is null)
        {
            McpGatewayInvocationTelemetry.ResultRead(false);
            return null;
        }
        var settings = response ?? new McpGatewayResponseOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(settings.MaxBytes, 1);
        if (options.Value.Governance.ManagedCatalog || stored.RequiresPublication)
        {
            settings = settings with { MaxBytes = Math.Min(settings.MaxBytes, options.Value.Governance.MaximumResponseBytes) };
        }
        if (stored.MaximumDeliveryBytes is { } maximumDeliveryBytes)
        {
            settings = settings with { MaxBytes = Math.Min(settings.MaxBytes, maximumDeliveryBytes) };
        }
        var result = stored.Result;
        if (settings.SelectPaths.Count > 0)
        {
            var projected = McpGatewayJsonProjection.Project(McpGatewayJsonSerializer.TrySerializeToNode(result), settings.SelectPaths);
            result = McpGatewayJsonSerializer.TrySerializeToElement(projected)
                ?? throw new InvalidOperationException("Stored result projection failed.");
        }
        var delivered = Encoding.UTF8.GetByteCount(result.GetRawText()) <= settings.MaxBytes;
        McpGatewayInvocationTelemetry.ResultRead(delivered);
        return delivered ? stored with { Result = result } : null;
    }

    public async Task<bool> RemoveAsync(string handle, CancellationToken cancellationToken = default)
    {
        var context = await McpGatewayTrustedContext.ResolveAsync(services, cancellationToken);
        return await ReadAuthorizedAsync(context, handle, cancellationToken) is not null
            && await McpGatewayTrustedContext.OperationServices(services).GetRequiredService<IMcpGatewayResultStore>().RemoveAsync(context, handle, cancellationToken);
    }

    private async Task<McpGatewayStoredResult?> ReadAuthorizedAsync(McpGatewayExecutionContext context, string handle, CancellationToken cancellationToken)
    {
        var stored = await McpGatewayTrustedContext.OperationServices(services).GetRequiredService<IMcpGatewayResultStore>().ReadAsync(context, handle, cancellationToken);
        if (stored is null || !(await McpGatewayAccessBoundary.AuthorizeAsync(services, context,
            new(McpGatewayOperation.ReadResult, stored.ToolId ?? handle, stored.SourceId) { DefinitionHash = stored.DefinitionHash }, cancellationToken)).IsAllowed)
        {
            return null;
        }
        if (options.Value.Governance.ManagedCatalog || stored.RequiresPublication)
        {
            var catalog = await McpGatewayTrustedContext.OperationServices(services).GetRequiredService<IMcpGatewayCatalogDefinitionStore>().ReadAsync(cancellationToken);
            if (stored.ToolId is null || catalog.RevokedToolIds.Contains(stored.ToolId)
                || !catalog.Definitions.Any(definition => definition.ToolId.Equals(stored.ToolId, StringComparison.OrdinalIgnoreCase)
                    && definition.DefinitionHash == stored.DefinitionHash && definition.Lifecycle == McpGatewayToolLifecycle.Published))
            {
                return null;
            }
        }
        return stored;
    }
}
