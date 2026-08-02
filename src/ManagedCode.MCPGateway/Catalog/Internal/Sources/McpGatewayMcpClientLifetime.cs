using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayMcpClientLifetime(bool disposeClient) : IAsyncDisposable
{
    private readonly bool _disposeClient = disposeClient;
    private McpClient? _client;
    private ClientOperation? _clientOperation;
    private int _disposed;

    internal bool HasCachedClient => Volatile.Read(ref _client) is not null;

    public async ValueTask<McpClient> GetAsync(
        Func<ILoggerFactory, CancellationToken, ValueTask<McpClient>> createClientAsync,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(createClientAsync);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (Volatile.Read(ref _client) is { } client)
        {
            return client;
        }

        var clientOperation = Volatile.Read(ref _clientOperation);
        while (!cancellationToken.IsCancellationRequested)
        {
            if (clientOperation is null)
            {
                var clientSource = new TaskCompletionSource<McpClient>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                );
                var createdOperation = new ClientOperation(clientSource.Task, cancellationToken);
                if (
                    Interlocked.CompareExchange(
                        ref _clientOperation,
                        createdOperation,
                        null
                    ) is null
                )
                {
                    _ = RunCreateClientAsync(
                        clientSource,
                        createClientAsync,
                        loggerFactory,
                        createdOperation
                    );
                    clientOperation = createdOperation;
                    break;
                }

                clientOperation = Volatile.Read(ref _clientOperation);
                continue;
            }

            if (clientOperation.CancellationToken.IsCancellationRequested)
            {
                await AwaitCanceledClientCreationAsync(clientOperation);
                _ = Interlocked.CompareExchange(
                    ref _clientOperation,
                    null,
                    clientOperation
                );
                clientOperation = Volatile.Read(ref _clientOperation);
                continue;
            }

            if (clientOperation.Task.IsCanceled || clientOperation.Task.IsFaulted)
            {
                _ = Interlocked.CompareExchange(
                    ref _clientOperation,
                    null,
                    clientOperation
                );
                clientOperation = Volatile.Read(ref _clientOperation);
                continue;
            }

            break;
        }

        if (clientOperation is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        return await AwaitClientTaskAsync(clientOperation!.Task, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_disposeClient && Interlocked.Exchange(ref _client, null) is { } client)
        {
            await client.DisposeAsync();
        }
    }

    private async Task RunCreateClientAsync(
        TaskCompletionSource<McpClient> clientSource,
        Func<ILoggerFactory, CancellationToken, ValueTask<McpClient>> createClientAsync,
        ILoggerFactory loggerFactory,
        ClientOperation clientOperation
    )
    {
        McpClient? createdClient = null;
        try
        {
            createdClient = await createClientAsync(
                loggerFactory,
                clientOperation.CancellationToken
            );
            var cachedClient = await CacheCreatedClientAsync(createdClient);
            createdClient = null;
            clientSource.SetResult(cachedClient);
        }
        catch (OperationCanceledException)
            when (clientOperation.CancellationToken.IsCancellationRequested)
        {
            clientSource.SetCanceled(clientOperation.CancellationToken);
        }
        catch (Exception exception)
        {
            if (createdClient is not null && _disposeClient)
            {
                try
                {
                    await createdClient.DisposeAsync();
                }
                catch (Exception cleanupException)
                {
                    exception = new AggregateException(exception, cleanupException);
                }
            }

            clientSource.SetException(exception);
        }
        finally
        {
            _ = Interlocked.CompareExchange(ref _clientOperation, null, clientOperation);
        }
    }

    private async Task<McpClient> AwaitClientTaskAsync(
        Task<McpClient> clientTask,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var client = await clientTask.WaitAsync(cancellationToken);
            if (Volatile.Read(ref _disposed) == 0)
            {
                return client;
            }

            if (_disposeClient)
            {
                await client.DisposeAsync();
            }

            throw new ObjectDisposedException(nameof(McpGatewayMcpClientLifetime));
        }
        catch when (clientTask.IsFaulted || clientTask.IsCanceled)
        {
            if (
                Volatile.Read(ref _clientOperation) is { Task: { } currentTask } currentOperation
                && ReferenceEquals(currentTask, clientTask)
            )
            {
                _ = Interlocked.CompareExchange(
                    ref _clientOperation,
                    null,
                    currentOperation
                );
            }

            throw;
        }
    }

    private static async Task AwaitCanceledClientCreationAsync(ClientOperation clientOperation)
    {
        try
        {
            await clientOperation.Task;
        }
        catch (OperationCanceledException)
            when (clientOperation.CancellationToken.IsCancellationRequested)
        {
            return;
        }
    }

    private async ValueTask<McpClient> CacheCreatedClientAsync(McpClient client)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var cachedClient = Volatile.Read(ref _client);
        if (cachedClient is null)
        {
            cachedClient = Interlocked.CompareExchange(ref _client, client, null);
            if (cachedClient is null)
            {
                return client;
            }
        }

        if (!ReferenceEquals(cachedClient, client) && _disposeClient)
        {
            await client.DisposeAsync();
        }

        return cachedClient;
    }

    private sealed record ClientOperation(
        Task<McpClient> Task,
        CancellationToken CancellationToken
    );
}
