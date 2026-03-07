using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayIndexWarmupService(
    IMcpGateway gateway,
    ILogger<McpGatewayIndexWarmupService> logger) : IHostedService
{
    private const string WarmupFailedLogMessage = "ManagedCode.MCPGateway background index warmup failed.";
    private Task? _warmupTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _warmupTask = WarmAsync(cancellationToken);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_warmupTask is null)
        {
            return;
        }

        try
        {
            await _warmupTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
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
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, WarmupFailedLogMessage);
        }
    }
}
