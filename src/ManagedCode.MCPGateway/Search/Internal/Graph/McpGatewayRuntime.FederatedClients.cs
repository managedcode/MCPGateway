using ManagedCode.MarkdownLd.Kb.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime
{
    private const string FederationBindingDeniedMessage = "The remote graph transport is unavailable under the configured caller binding and egress policy.";
    private const string FederationEndpointsArgumentName = "serviceEndpoints";
    private const string FederationQueryDeniedMessage = "The federated query is unavailable under the configured data egress policy.";

    private async Task<FederatedClientLeases> ResolveFederatedClientsAsync(IReadOnlyList<Uri> endpoints,
        CancellationToken cancellationToken)
    {
        var leases = new FederatedClientLeases();
        var services = McpGatewayTrustedContext.OperationServices(_serviceProvider);
        var context = await McpGatewayTrustedContext.ResolveAsync(services, cancellationToken);
        try
        {
            foreach (var endpoint in endpoints)
            {
                if (endpoint == _graphLocalFederationEndpoint)
                {
                    continue;
                }
                var binding = _federatedServiceBindings.FirstOrDefault(binding =>
                    string.Equals(binding.Endpoint.AbsoluteUri, endpoint.AbsoluteUri, StringComparison.Ordinal));
                if (binding is null && !_managedCatalog)
                {
                    continue;
                }
                if (binding is null || services.GetService<IMcpGatewayFederatedClientResolver>() is not { } resolver)
                {
                    throw new UnauthorizedAccessException(FederationBindingDeniedMessage);
                }
                await McpGatewayAccessBoundary.DemandAsync(services, McpGatewayOperation.FederateGraph,
                    binding.BindingReference, binding.SourceId, cancellationToken);
                var lease = await resolver.ResolveAsync(context, binding, cancellationToken);
                leases.Leases.Add(lease);
                if (!context.IsAuthenticated || lease.BindingReference != binding.BindingReference
                    || lease.Audience != binding.Audience || lease.CredentialRevision != context.CredentialRevision
                    || binding.RequiredScopes.Any(scope => !lease.GrantedScopes.Contains(scope))
                    || _managedCatalog && (lease.Client is not McpGatewayEgressHttpClient.PolicyHttpClient client
                        || !string.Equals(client.SourceId, binding.SourceId, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new UnauthorizedAccessException(FederationBindingDeniedMessage);
                }
                leases.Bindings.Add(new(endpoint, lease.Client));
            }
            return leases;
        }
        catch
        {
            await leases.DisposeAsync();
            throw;
        }
    }

    private sealed class FederatedClientLeases : IAsyncDisposable
    {
        public List<IMcpGatewayFederatedClientLease> Leases { get; } = [];
        public List<FederatedSparqlRemoteServiceBinding> Bindings { get; } = [];
        public async ValueTask DisposeAsync()
        {
            List<Exception>? failures = null;
            foreach (var lease in Leases)
            {
                try
                { await lease.DisposeAsync(); }
                catch (Exception exception) { (failures ??= []).Add(exception); }
            }
            if (failures is not null)
            { throw new AggregateException(failures); }
        }
    }
}
