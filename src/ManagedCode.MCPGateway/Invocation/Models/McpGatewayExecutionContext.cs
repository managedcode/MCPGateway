namespace ManagedCode.MCPGateway;

/// <summary>Authenticated host data. Model-supplied Context and arguments never populate this type.</summary>
public sealed record McpGatewayExecutionContext(string SubjectId, string TenantId, string AuthorizationPartition, string PolicyRevision)
{
    public System.Collections.Immutable.ImmutableDictionary<string, string> Claims { get; init; } =
        System.Collections.Immutable.ImmutableDictionary<string, string>.Empty;
    public string? ApplicationId { get; init; }
    public string? CorrelationId { get; init; }
    public string CredentialRevision { get; init; } = string.Empty;
    public DateTimeOffset? Deadline { get; init; }
    public bool IsAuthenticated { get; init; } = true;
    internal static McpGatewayExecutionContext TrustedLocal { get; } = new(TrustedLocalId, TrustedLocalId, TrustedLocalId, TrustedLocalId);
    private const string TrustedLocalId = "trusted-local";
}
