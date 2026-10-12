using System.Net;
using System.Net.Sockets;

namespace ManagedCode.MCPGateway;

/// <summary>Host-owned standard HTTP pipeline; redirects never silently forward caller credentials.</summary>
public static class McpGatewayEgressHttpClient
{
    private static readonly TimeSpan PerRequestConnectionLifetime = TimeSpan.Zero;
    private const string DeniedMessage = "The outbound destination is unavailable under the configured egress policy.";

    public static HttpClient Create(string sourceId, IMcpGatewayEgressPolicy policy,
        IMcpGatewayExecutionContextProvider contextProvider, SocketsHttpHandler transport)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(contextProvider);
        ArgumentNullException.ThrowIfNull(transport);
        if (transport.ConnectCallback is not null)
        {
            throw new ArgumentException("Supply a transport without an existing connection callback.", nameof(transport));
        }
        transport.AllowAutoRedirect = false;
        transport.UseProxy = false;
        transport.PooledConnectionLifetime = PerRequestConnectionLifetime;
        transport.ConnectCallback = async (connection, cancellationToken) =>
        {
            var destination = connection.InitialRequestMessage.RequestUri ?? throw new HttpRequestException(DeniedMessage);
            var context = await contextProvider.GetCurrentAsync(cancellationToken);
            var addresses = await Dns.GetHostAddressesAsync(connection.DnsEndPoint.Host, cancellationToken);
            foreach (var address in addresses)
            {
                if (!context.IsAuthenticated || !await policy.IsAllowedAsync(context,
                    new(sourceId, destination) { ResolvedAddress = address }, cancellationToken))
                {
                    continue;
                }
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, connection.DnsEndPoint.Port), cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (SocketException)
                {
                    socket.Dispose();
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
            throw new HttpRequestException(DeniedMessage);
        };
        return new PolicyHttpClient(sourceId, new PolicyHandler(sourceId, policy, contextProvider) { InnerHandler = transport });
    }

    internal sealed class PolicyHttpClient(string sourceId, HttpMessageHandler handler) : HttpClient(handler, disposeHandler: true)
    {
        public string SourceId { get; } = sourceId;
    }

    private sealed class PolicyHandler(string sourceId, IMcpGatewayEgressPolicy policy,
        IMcpGatewayExecutionContextProvider contextProvider) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var destination = request.RequestUri ?? throw new HttpRequestException(DeniedMessage);
            var context = await contextProvider.GetCurrentAsync(cancellationToken);
            if (!context.IsAuthenticated || !destination.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                    && !destination.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || !await policy.IsAllowedAsync(context, new(sourceId, destination), cancellationToken))
            {
                throw new HttpRequestException(DeniedMessage);
            }
            var response = await base.SendAsync(request, cancellationToken);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                response.Dispose();
                throw new HttpRequestException(DeniedMessage);
            }
            return response;
        }
    }
}
