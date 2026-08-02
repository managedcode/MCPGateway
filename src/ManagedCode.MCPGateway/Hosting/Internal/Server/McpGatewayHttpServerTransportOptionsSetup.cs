#pragma warning disable MCPEXP002

using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayHttpServerTransportOptionsSetup
    : IPostConfigureOptions<HttpServerTransportOptions>
{
    public void PostConfigure(string? name, HttpServerTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Stateless = true;
    }
}

#pragma warning restore MCPEXP002
