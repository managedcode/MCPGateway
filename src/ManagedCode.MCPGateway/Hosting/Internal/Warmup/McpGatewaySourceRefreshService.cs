using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewaySourceRefreshService(IMcpGatewayCatalogRuntime catalog,
    IOptions<McpGatewaySourceRefreshOptions> options, ILogger<McpGatewaySourceRefreshService> logger,
    TimeProvider clock) : BackgroundService
{
    private const string RefreshFailedMessage = "Scheduled source refresh failed; the previous validated catalog remains active.";
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(settings.Interval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(settings.InitialBackoff, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(settings.MaximumBackoff, settings.InitialBackoff);
        ArgumentOutOfRangeException.ThrowIfLessThan(settings.MaxConcurrency, 1);
        var sources = settings.SourceIds.Distinct(StringComparer.OrdinalIgnoreCase).Select(id =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            return new SourceState(id, settings.InitialBackoff) { NextAttempt = clock.GetUtcNow() + settings.Interval };
        }).ToArray();
        if (sources.Length == 0)
        { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = sources.Min(source => source.NextAttempt) - clock.GetUtcNow();
            if (wait > TimeSpan.Zero)
            { await Task.Delay(wait, clock, stoppingToken); }
            await Parallel.ForEachAsync(sources, new ParallelOptions
            { MaxDegreeOfParallelism = settings.MaxConcurrency, CancellationToken = stoppingToken }, async (source, token) =>
        {
            if (source.NextAttempt > clock.GetUtcNow())
            { return; }
            try
            {
                await catalog.RefreshSourceAsync(source.Id, token);
                source.Backoff = settings.InitialBackoff;
                source.NextAttempt = clock.GetUtcNow() + settings.Interval;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception) when (!McpGatewayRuntimeFailureClassifier.HasFatalFailure(exception))
            {
                source.NextAttempt = clock.GetUtcNow() + source.Backoff;
                source.Backoff = TimeSpan.FromTicks(Math.Min(source.Backoff.Ticks, settings.MaximumBackoff.Ticks / 2) * 2);
                logger.LogWarning(RefreshFailedMessage);
            }
        });
        }
    }
    private sealed class SourceState(string id, TimeSpan backoff)
    {
        public string Id { get; } = id;
        public TimeSpan Backoff { get; set; } = backoff;
        public DateTimeOffset NextAttempt { get; set; }
    }
}
