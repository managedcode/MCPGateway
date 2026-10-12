using ModelContextProtocol.Client;

namespace ManagedCode.MCPGateway;

/// <summary>The host supplies a caller-bound SDK client without mutating shared transport credentials.</summary>
public interface IMcpGatewayExecutionClientResolver
{
    ValueTask<IMcpGatewayExecutionClientLease> ResolveAsync(McpGatewayExecutionContext context,
        McpGatewayRemoteBinding binding, CancellationToken cancellationToken = default);
}

public interface IMcpGatewayExecutionClientLease : IAsyncDisposable
{
    McpClient Client { get; }
    string BindingReference { get; }
    string Audience { get; }
    string CredentialRevision { get; }
    IReadOnlySet<string> GrantedScopes { get; }
}

public sealed record McpGatewayRemoteBinding(string SourceId, string BindingReference, string Audience, IReadOnlyList<string> RequiredScopes)
{
    /// <summary>Explicit approval to execute through the catalog's technical client.</summary>
    public bool UseSharedTechnicalClient { get; init; }
}
