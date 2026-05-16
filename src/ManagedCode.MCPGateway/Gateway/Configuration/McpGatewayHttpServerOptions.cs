#pragma warning disable MCPEXP001

using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;

namespace ManagedCode.MCPGateway;

public sealed class McpGatewayHttpServerOptions
{
    public string SourceId { get; set; } = string.Empty;

    public Uri? Endpoint { get; set; }

    public string? DisplayName { get; set; }

    public HttpTransportMode TransportMode { get; set; } = HttpTransportMode.StreamableHttp;

    public IReadOnlyDictionary<string, string>? AdditionalHeaders { get; set; }

    public TimeSpan? ConnectionTimeout { get; set; }

    public string? KnownSessionId { get; set; }

    public bool? OwnsSession { get; set; }

    public ClientOAuthOptions? OAuth { get; set; }

    public int? MaxReconnectionAttempts { get; set; }

    public TimeSpan? DefaultReconnectionInterval { get; set; }

    internal McpGatewayHttpServerOptions CloneWithSourceId(string sourceId) =>
        new()
        {
            SourceId = sourceId,
            Endpoint = Endpoint,
            DisplayName = DisplayName,
            TransportMode = TransportMode,
            AdditionalHeaders = AdditionalHeaders is null
                ? null
                : new Dictionary<string, string>(AdditionalHeaders, StringComparer.Ordinal),
            ConnectionTimeout = ConnectionTimeout,
            KnownSessionId = KnownSessionId,
            OwnsSession = OwnsSession,
            OAuth = OAuth,
            MaxReconnectionAttempts = MaxReconnectionAttempts,
            DefaultReconnectionInterval = DefaultReconnectionInterval,
        };
}
