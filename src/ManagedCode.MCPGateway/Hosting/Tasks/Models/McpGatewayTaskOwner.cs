namespace ManagedCode.MCPGateway;

/// <summary>The trusted authorization and source binding persisted with a task.</summary>
public sealed record McpGatewayTaskOwner(
    string SubjectId,
    string TenantId,
    string AuthorizationPartition,
    string PolicyRevision,
    string? AssetId,
    string? DefinitionHash);
