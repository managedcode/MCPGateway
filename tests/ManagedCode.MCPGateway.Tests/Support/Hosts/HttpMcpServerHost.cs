using System.ComponentModel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace ManagedCode.MCPGateway.Tests;

internal sealed class HttpMcpServerHost(WebApplication application, Uri endpoint)
    : IAsyncDisposable
{
    public Uri Endpoint { get; } = endpoint;

    public static async Task<HttpMcpServerHost> StartAsync(
        IReadOnlyDictionary<string, string>? requiredHeaders = null,
        CancellationToken cancellationToken = default
    )
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddMcpServer().WithHttpTransport().WithTools<HttpMcpTools>();

        var application = builder.Build();
        if (requiredHeaders is { Count: > 0 })
        {
            application.Use(async (context, next) =>
            {
                foreach (var (headerName, headerValue) in requiredHeaders)
                {
                    if (!context.Request.Headers.TryGetValue(headerName, out var actualValue) ||
                        !string.Equals(actualValue.ToString(), headerValue, StringComparison.Ordinal))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return;
                    }
                }

                await next(context);
            });
        }

        application.MapMcp("/mcp");
        await application.StartAsync(cancellationToken);

        var address = application
            .Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()
            ?.Addresses.Single()
            ?? throw new InvalidOperationException("HTTP MCP server address was not assigned.");

        return new HttpMcpServerHost(application, new Uri(new Uri(address), "/mcp"));
    }

    public async ValueTask DisposeAsync()
    {
        await application.StopAsync();
        await application.DisposeAsync();
    }

    [McpServerToolType]
    private sealed class HttpMcpTools
    {
        [McpServerTool(
            Name = "streamable_http_lookup",
            Title = "Streamable HTTP lookup",
            Idempotent = true,
            ReadOnly = true,
            UseStructuredContent = false
        )]
        [Description("Return a value from an HTTP MCP server.")]
        public static string Lookup([Description("Lookup query.")] string query) =>
            $"streamable-http:{query}";
    }
}
