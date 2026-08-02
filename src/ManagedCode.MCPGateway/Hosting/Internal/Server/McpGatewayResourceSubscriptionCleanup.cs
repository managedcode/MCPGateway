using System.Runtime.ExceptionServices;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayResourceSubscriptionCleanup(
    McpGatewayMcpServerBindingManager bindingManager
)
{
    public async ValueTask DisposeAsync(
        IAsyncDisposable? subscription,
        bool releasePinnedBinding,
        ModelContextProtocol.Server.McpServer downstreamServer,
        List<Exception> cleanupExceptions
    )
    {
        if (subscription is not null)
        {
            try
            {
                await subscription.DisposeAsync();
            }
            catch (Exception exception)
            {
                cleanupExceptions.Add(exception);
            }
        }

        if (releasePinnedBinding)
        {
            try
            {
                await bindingManager.ReleaseAsync(downstreamServer);
            }
            catch (Exception exception)
            {
                cleanupExceptions.Add(exception);
            }
        }
    }

    public static void ThrowIfFailed(List<Exception> cleanupExceptions)
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
}
