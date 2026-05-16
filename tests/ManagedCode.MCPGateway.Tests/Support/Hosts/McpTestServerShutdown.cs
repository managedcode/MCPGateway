namespace ManagedCode.MCPGateway.Tests;

internal static class McpTestServerShutdown
{
    public static async ValueTask AwaitServerStopAsync(
        Task serverTask,
        CancellationToken expectedCancellationToken
    )
    {
        try
        {
            await serverTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (expectedCancellationToken.IsCancellationRequested)
        {
            return;
        }
    }
}
