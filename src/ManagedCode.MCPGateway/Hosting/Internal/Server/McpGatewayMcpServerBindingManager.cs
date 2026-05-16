using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using ManagedCode.MCPGateway.Abstractions;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayMcpServerBindingManager(
    IMcpGatewayServerBindingResolver bindingResolver
) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, SessionBindingState> _sessionBindings =
        new(StringComparer.Ordinal);
    private int _disposed;

    internal int SessionBindingCount => _sessionBindings.Count;

    public async ValueTask<McpGatewayServerBindingLease> AcquireAsync(
        IServiceProvider? requestServices,
        IServiceProvider serverServices,
        ModelContextProtocol.Server.McpServer server,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(serverServices);
        ArgumentNullException.ThrowIfNull(server);
        ThrowIfDisposed();

        var sessionKey = McpGatewayMcpServerIdentity.GetKey(server);
        if (_sessionBindings.TryGetValue(sessionKey, out var pinnedBinding))
        {
            return new McpGatewayServerBindingLease(
                await WaitForBindingAsync(sessionKey, pinnedBinding, cancellationToken),
                ownsBinding: false
            );
        }

        var binding = await bindingResolver.ResolveAsync(
            requestServices,
            serverServices,
            server,
            cancellationToken
        );

        return new McpGatewayServerBindingLease(binding, ownsBinding: true);
    }

    public async ValueTask<McpGatewayServerBindingLease> PinAsync(
        IServiceProvider? requestServices,
        IServiceProvider serverServices,
        ModelContextProtocol.Server.McpServer server,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(serverServices);
        ArgumentNullException.ThrowIfNull(server);
        ThrowIfDisposed();

        var sessionKey = McpGatewayMcpServerIdentity.GetKey(server);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();

            if (_sessionBindings.TryGetValue(sessionKey, out var existingBinding))
            {
                existingBinding.AddReference();
                try
                {
                    return new McpGatewayServerBindingLease(
                        await WaitForBindingAsync(sessionKey, existingBinding, cancellationToken),
                        ownsBinding: false
                    );
                }
                catch (OperationCanceledException) when (!existingBinding.BindingTask.IsCompleted)
                {
                    await ReleaseAsync(server);
                    throw;
                }
            }

            var createdBinding = SessionBindingState.Create(
                bindingResolver,
                requestServices,
                serverServices,
                server,
                CancellationToken.None
            );

            if (_sessionBindings.TryAdd(sessionKey, createdBinding))
            {
                try
                {
                    if (Volatile.Read(ref _disposed) != 0)
                    {
                        if (
                            _sessionBindings.TryRemove(
                                new KeyValuePair<string, SessionBindingState>(
                                    sessionKey,
                                    createdBinding
                                )
                            )
                        )
                        {
                            await createdBinding.DisposeAsync();
                        }

                        ThrowIfDisposed();
                    }

                    return new McpGatewayServerBindingLease(
                        await WaitForBindingAsync(sessionKey, createdBinding, cancellationToken),
                        ownsBinding: false
                    );
                }
                catch (OperationCanceledException) when (!createdBinding.BindingTask.IsCompleted)
                {
                    await ReleaseAsync(server);
                    throw;
                }
            }

            await createdBinding.DisposeAsync();
        }
    }

    public async ValueTask ReleaseAsync(ModelContextProtocol.Server.McpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        var sessionKey = McpGatewayMcpServerIdentity.GetKey(server);
        if (!_sessionBindings.TryGetValue(sessionKey, out var bindingState))
        {
            return;
        }

        if (bindingState.ReleaseReference() > 0)
        {
            return;
        }

        if (
            _sessionBindings.TryRemove(
                new KeyValuePair<string, SessionBindingState>(sessionKey, bindingState)
            )
        )
        {
            await bindingState.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var bindings = _sessionBindings.Values.ToArray();
        _sessionBindings.Clear();
        var cleanupExceptions = new List<Exception>();

        foreach (var bindingState in bindings)
        {
            try
            {
                await bindingState.DisposeAsync();
            }
            catch (Exception exception)
            {
                cleanupExceptions.Add(exception);
            }
        }

        ThrowIfCleanupFailed(cleanupExceptions);
    }

    internal readonly struct McpGatewayServerBindingLease(IMcpGatewayServerBinding binding, bool ownsBinding) : IAsyncDisposable
    {
        public IMcpGatewayServerBinding Binding { get; } = binding;

        public bool OwnsBinding { get; } = ownsBinding;

        public ValueTask DisposeAsync() => OwnsBinding ? Binding.DisposeAsync() : ValueTask.CompletedTask;
    }

    private async Task<IMcpGatewayServerBinding> WaitForBindingAsync(
        string sessionKey,
        SessionBindingState bindingState,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return await bindingState.BindingTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (!bindingState.BindingTask.IsCompleted)
        {
            throw;
        }
        catch
        {
            if (
                _sessionBindings.TryRemove(
                    new KeyValuePair<string, SessionBindingState>(sessionKey, bindingState)
                )
            )
            {
                await bindingState.DisposeAsync();
            }

            throw;
        }
    }

    private static void ThrowIfCleanupFailed(List<Exception> cleanupExceptions)
    {
        switch (cleanupExceptions.Count)
        {
            case 0:
                return;
            case 1:
                ExceptionDispatchInfo.Capture(cleanupExceptions[0]).Throw();
                break;
            default:
                throw new AggregateException(cleanupExceptions);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private sealed class SessionBindingState(
        Task<IMcpGatewayServerBinding> bindingTask,
        CancellationTokenSource resolutionCancellation
    )
        : IAsyncDisposable
    {
        private int _references = 1;
        private int _disposed;

        public Task<IMcpGatewayServerBinding> BindingTask { get; } = bindingTask;

        public static SessionBindingState Create(
            IMcpGatewayServerBindingResolver resolver,
            IServiceProvider? requestServices,
            IServiceProvider serverServices,
            ModelContextProtocol.Server.McpServer server,
            CancellationToken cancellationToken
        )
        {
            var resolutionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken
            );
            return new(
                resolver
                    .ResolveAsync(
                        requestServices,
                        serverServices,
                        server,
                        resolutionCancellation.Token
                    )
                    .AsTask(),
                resolutionCancellation
            );
        }

        public void AddReference() => Interlocked.Increment(ref _references);

        public int ReleaseReference() => Interlocked.Decrement(ref _references);

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await resolutionCancellation.CancelAsync();

            IMcpGatewayServerBinding binding;
            try
            {
                binding = await BindingTask;
            }
            catch
            {
                // Resolution failures have nothing to dispose and should not fail cleanup.
                return;
            }
            finally
            {
                resolutionCancellation.Dispose();
            }

            await binding.DisposeAsync();
        }
    }
}
