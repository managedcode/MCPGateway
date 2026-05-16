#pragma warning disable MCPEXP002

using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayHttpServerTransportOptionsSetup(
    McpGatewayMcpServerSessionCleaner sessionCleaner
) : IPostConfigureOptions<HttpServerTransportOptions>
{
    public void PostConfigure(string? name, HttpServerTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var runSessionHandler = options.RunSessionHandler;
        options.RunSessionHandler = async (httpContext, server, cancellationToken) =>
            await RunSessionAndCleanUpAsync(runSessionHandler, httpContext, server, cancellationToken);
    }

    private async Task RunSessionAndCleanUpAsync(
        Func<Microsoft.AspNetCore.Http.HttpContext, McpServer, CancellationToken, Task>? runSessionHandler,
        Microsoft.AspNetCore.Http.HttpContext httpContext,
        McpServer server,
        CancellationToken cancellationToken
    )
    {
        ExceptionDispatchInfo? sessionException = null;

        try
        {
            if (runSessionHandler is not null)
            {
                await runSessionHandler(httpContext, server, cancellationToken);
            }
            else
            {
                await server.RunAsync(cancellationToken);
            }
        }
        catch (Exception exception)
        {
            sessionException = ExceptionDispatchInfo.Capture(exception);
        }

        ExceptionDispatchInfo? cleanupException = null;
        try
        {
            await sessionCleaner.RemoveSessionAsync(server);
        }
        catch (Exception exception)
        {
            cleanupException = ExceptionDispatchInfo.Capture(exception);
        }

        if (cleanupException is not null)
        {
            if (
                sessionException?.SourceException is OperationCanceledException
                && cancellationToken.IsCancellationRequested
            )
            {
                cleanupException.Throw();
            }

            if (sessionException is not null)
            {
                throw new AggregateException(
                    sessionException.SourceException,
                    cleanupException.SourceException
                );
            }

            cleanupException.Throw();
        }

        sessionException?.Throw();
    }
}

#pragma warning restore MCPEXP002
