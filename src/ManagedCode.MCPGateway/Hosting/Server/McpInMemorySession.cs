using System.IO.Pipelines;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ManagedCode.MCPGateway;

/// <summary>Runs an SDK MCP server and client in one process while preserving request-scoped services.</summary>
public sealed class McpInMemorySession : IAsyncDisposable
{
    private readonly McpServer _server;
    private readonly Task _serverTask;
    private readonly CancellationTokenSource _lifetime;
    private readonly object _disposalSync = new();
    private Task? _disposal;

    private McpInMemorySession(McpClient client, McpServer server, Task serverTask,
        CancellationTokenSource lifetime)
    {
        Client = client;
        _server = server;
        _serverTask = serverTask;
        _lifetime = lifetime;
    }

    public McpClient Client { get; }

    public static async Task<McpInMemorySession> CreateAsync(McpServerOptions options,
        IServiceProvider services, ILoggerFactory loggerFactory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        cancellationToken.ThrowIfCancellationRequested();

        var toServer = new Pipe();
        var toClient = new Pipe();
        var clientTransport = new StreamClientTransport(
            toServer.Writer.AsStream(), toClient.Reader.AsStream(), loggerFactory);
        var serverTransport = new StreamServerTransport(
            toServer.Reader.AsStream(), toClient.Writer.AsStream(), loggerFactory: loggerFactory);
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var server = McpServer.Create(serverTransport, options, loggerFactory, services);
        var serverTask = server.RunAsync(lifetime.Token);
        try
        {
            var client = await McpClient.CreateAsync(clientTransport,
                new McpClientOptions(), loggerFactory, lifetime.Token).ConfigureAwait(false);
            return new McpInMemorySession(client, server, serverTask, lifetime);
        }
        catch
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            await toServer.Writer.CompleteAsync().ConfigureAwait(false);
            await toClient.Reader.CompleteAsync().ConfigureAwait(false);
            await server.DisposeAsync().ConfigureAwait(false);
            try
            {
                await serverTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
            finally
            {
                lifetime.Dispose();
            }

            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposalSync)
        {
            return new ValueTask(_disposal ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            await Client.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            await _server.DisposeAsync().ConfigureAwait(false);
            try
            {
                await _serverTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
            finally
            {
                _lifetime.Dispose();
            }
        }
    }
}
