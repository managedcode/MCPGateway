using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway;

internal static class McpGatewayAccessBoundary
{
    private const string UnavailableMessage = "The requested gateway capability is unavailable.";

    public static async ValueTask<McpGatewayAccessDecision> AuthorizeAsync(IServiceProvider services,
        McpGatewayExecutionContext context, McpGatewayAccessRequest request, CancellationToken cancellationToken)
    {
        services = McpGatewayTrustedContext.OperationServices(services);
        if (services.GetService<IMcpGatewayAccessPolicy>() is not { } policy)
        {
            return McpGatewayAccessDecision.Allowed;
        }
        return !context.IsAuthenticated ? McpGatewayAccessDecision.Denied
            : await policy.AuthorizeAsync(context, request, cancellationToken);
    }

    public static async ValueTask DemandAsync(IServiceProvider services, McpGatewayOperation operation, string assetId,
        string? sourceId, CancellationToken cancellationToken)
    {
        var context = await McpGatewayTrustedContext.ResolveAsync(services, cancellationToken);
        if (!(await AuthorizeAsync(services, context, new(operation, assetId, sourceId), cancellationToken)).IsAllowed)
        {
            throw new UnauthorizedAccessException(UnavailableMessage);
        }
    }

    private static bool IsRegistered<T>(IServiceProvider services) =>
        services.GetService<IServiceProviderIsService>()?.IsService(typeof(T)) ?? services.GetService<T>() is not null;

    public static void ValidateServices(IServiceProvider services, McpGatewayOptions options)
    {
        if (IsRegistered<IMcpGatewayAccessPolicy>(services)
            && !IsRegistered<IMcpGatewayExecutionContextProvider>(services))
        {
            throw new InvalidOperationException("A gateway access policy requires a trusted execution-context provider.");
        }
        if (options.Governance.ManagedCatalog && (!IsRegistered<IMcpGatewayAccessPolicy>(services)
            || !IsRegistered<IMcpGatewayExecutionContextProvider>(services)
            || !IsRegistered<IMcpGatewayCatalogDefinitionStore>(services)))
        {
            throw new InvalidOperationException("Managed catalog mode requires a trusted context provider, access policy and catalog definition store.");
        }
        if (options.Governance.ManagedCatalog && options.SourceRegistrations.Any(source =>
            source is McpGatewayHttpToolSourceRegistration { HasControlledHttpClient: false }))
        {
            throw new InvalidOperationException("Managed HTTP sources require a source-bound McpGatewayEgressHttpClient before discovery.");
        }
        if (IsRegistered<IMcpGatewayAccessPolicy>(services)
            && options.MarkdownLdGraphSource != McpGatewayMarkdownLdGraphSource.GeneratedToolGraph
            && !IsRegistered<IMcpGatewayAuthorizedGraphFactory>(services))
        {
            throw new InvalidOperationException("Application-owned graphs require an authorized graph factory when an access policy is enabled.");
        }
    }
}
