using Microsoft.Extensions.Options;

namespace ManagedCode.MCPGateway;

/// <summary>Finite source controllers; hosts may replace this with distributed admission.</summary>
internal sealed class McpGatewaySourceAdmission : IMcpGatewaySourceAdmission, IAsyncDisposable
{
    private readonly Dictionary<string, SourceController> _sources;
    private readonly TimeProvider _clock;
    private readonly McpGatewayOperationGate _lifetime = new();

    public McpGatewaySourceAdmission(IOptions<McpGatewayOptions> options, TimeProvider clock)
    {
        _clock = clock;
        _sources = new(StringComparer.OrdinalIgnoreCase);
        var execution = options.Value.Execution.Snapshot();
        foreach (var sourceId in options.Value.SourceRegistrations.Select(source => source.SourceId)
            .Concat(execution.Sources.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var setting = execution.Sources.GetValueOrDefault(sourceId) ?? new McpGatewaySourceExecutionOptions();
            ArgumentOutOfRangeException.ThrowIfLessThan(setting.MaxConcurrency, 1);
            ArgumentOutOfRangeException.ThrowIfNegative(setting.QueueCapacity);
            ArgumentOutOfRangeException.ThrowIfLessThan(setting.FailureThreshold, 1);
            if (setting.QueueTimeout <= TimeSpan.Zero || setting.ExecutionTimeout <= TimeSpan.Zero || setting.CircuitBreakDuration <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(options));
            }
            _sources.Add(sourceId, new(setting));
        }
    }

    public async ValueTask<IMcpGatewayExecutionLease?> TryEnterAsync(McpGatewayExecutionContext context, string sourceId, CancellationToken cancellationToken = default)
    {
        _lifetime.Enter(this);
        if (!_sources.TryGetValue(sourceId, out var source) || Volatile.Read(ref source.OpenUntilTicks) > _clock.GetUtcNow().Ticks)
        {
            _lifetime.Exit();
            return null;
        }
        if (Interlocked.Increment(ref source.Pending) > source.Settings.MaxConcurrency + source.Settings.QueueCapacity)
        {
            Interlocked.Decrement(ref source.Pending);
            _lifetime.Exit();
            return null;
        }
        var entered = false;
        try
        {
            var wait = source.Settings.QueueTimeout;
            if (context.Deadline is { } deadline)
            {
                wait = TimeSpan.FromTicks(Math.Min(wait.Ticks, (deadline - _clock.GetUtcNow()).Ticks));
            }
            if (wait <= TimeSpan.Zero)
            {
                return null;
            }
            using var deadlineCancellation = new CancellationTokenSource(wait, _clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadlineCancellation.Token);
            try
            {
                await source.Semaphore.WaitAsync(linked.Token);
                entered = true;
                return new Lease(source, _clock, _lifetime);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }
        }
        finally
        {
            if (!entered)
            {
                Interlocked.Decrement(ref source.Pending);
                _lifetime.Exit();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_lifetime.TryStartDispose(out var drain))
        {
            return;
        }
        await drain;
        foreach (var source in _sources.Values)
        {
            source.Semaphore.Dispose();
        }
    }

    private sealed class SourceController(McpGatewaySourceExecutionOptions settings)
    {
        public McpGatewaySourceExecutionOptions Settings { get; } = settings;
        public SemaphoreSlim Semaphore { get; } = new(settings.MaxConcurrency, settings.MaxConcurrency);
        public int Pending;
        public int Failures;
        public long OpenUntilTicks;
    }

    private sealed class Lease(SourceController source, TimeProvider clock, McpGatewayOperationGate lifetime) : IMcpGatewayExecutionLease
    {
        private int _disposed;
        public void Complete(bool upstreamUnavailable)
        {
            if (!upstreamUnavailable)
            {
                Interlocked.Exchange(ref source.Failures, 0);
                return;
            }
            if (Interlocked.Increment(ref source.Failures) >= source.Settings.FailureThreshold)
            {
                Interlocked.Exchange(ref source.OpenUntilTicks, (clock.GetUtcNow() + source.Settings.CircuitBreakDuration).Ticks);
                Interlocked.Exchange(ref source.Failures, 0);
            }
        }
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                source.Semaphore.Release();
                Interlocked.Decrement(ref source.Pending);
                lifetime.Exit();
            }
            return ValueTask.CompletedTask;
        }
    }
}
