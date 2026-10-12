namespace ManagedCode.MCPGateway;

/// <summary>Resolves a host-owned HTTP transport for the trusted caller. Leases remain alive until all SERVICE queries complete.</summary>
public interface IMcpGatewayFederatedClientResolver
{
    ValueTask<IMcpGatewayFederatedClientLease> ResolveAsync(McpGatewayExecutionContext context,
        McpGatewayFederatedServiceBinding binding, CancellationToken cancellationToken = default);
}

public interface IMcpGatewayFederatedClientLease : IAsyncDisposable
{
    HttpClient Client { get; }
    string BindingReference { get; }
    string Audience { get; }
    string CredentialRevision { get; }
    IReadOnlySet<string> GrantedScopes { get; }
}

public sealed record McpGatewayFederatedServiceBinding(Uri Endpoint, string SourceId,
    string BindingReference, string Audience, IReadOnlyList<string> RequiredScopes);
