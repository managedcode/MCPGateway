#pragma warning disable MCPEXP001

using ModelContextProtocol.Authentication;

namespace ManagedCode.MCPGateway;

public sealed class McpGatewayHttpServerOptions
{
    public string SourceId { get; set; } = string.Empty;

    public Uri? Endpoint { get; set; }

    public string? DisplayName { get; set; }

    public IReadOnlyDictionary<string, string>? AdditionalHeaders { get; set; }

    public TimeSpan? ConnectionTimeout { get; set; }

    public ClientOAuthOptions? OAuth { get; set; }

    /// <summary>Optional host-owned SDK HTTP pipeline, including explicit egress controls.</summary>
    public HttpClient? HttpClient { get; set; }

    internal McpGatewayHttpServerOptions CloneWithSourceId(string sourceId) =>
        new()
        {
            SourceId = sourceId,
            Endpoint = Endpoint,
            DisplayName = DisplayName,
            AdditionalHeaders = AdditionalHeaders is null
                ? null
                : new Dictionary<string, string>(AdditionalHeaders, StringComparer.Ordinal),
            ConnectionTimeout = ConnectionTimeout,
            OAuth = OAuth,
            HttpClient = HttpClient,
        };
}
