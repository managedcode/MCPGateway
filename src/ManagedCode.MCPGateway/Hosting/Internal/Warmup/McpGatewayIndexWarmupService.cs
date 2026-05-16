using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayIndexWarmupService(
    IMcpGateway gateway,
    ILogger<McpGatewayIndexWarmupService> logger
) : IHostedService
{
    private const string WarmupFailedLogMessage =
        "ManagedCode.MCPGateway background index warmup failed.";
    private CancellationTokenSource? _warmupCancellation;
    private Task? _warmupTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _warmupCancellation?.Cancel();
        _warmupCancellation?.Dispose();
        _warmupCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _warmupTask = WarmAsync(_warmupCancellation.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var warmupTask = _warmupTask;
        var warmupCancellation = _warmupCancellation;
        if (warmupTask is null)
        {
            return;
        }

        warmupCancellation?.Cancel();

        try
        {
            await warmupTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested
                || warmupCancellation?.IsCancellationRequested == true)
        {
            return;
        }
        finally
        {
            warmupCancellation?.Dispose();
            _warmupCancellation = null;
            _warmupTask = null;
        }
    }

    private async Task WarmAsync(CancellationToken cancellationToken)
    {
        try
        {
            await gateway.BuildIndexAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, WarmupFailedLogMessage);
        }
    }
}
