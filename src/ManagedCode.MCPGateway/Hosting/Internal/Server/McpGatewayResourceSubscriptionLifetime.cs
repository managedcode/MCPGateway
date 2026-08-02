namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayResourceSubscriptionLifetime(
    McpGatewayResourceSubscriptionRegistry registry,
    McpGatewayResourceSubscriptionCleanup cleanup
) : IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        if (!registry.TryBeginDispose(out var subscriptions))
        {
            return;
        }

        var cleanupExceptions = new List<Exception>();

        foreach (var (_, state) in subscriptions)
        {
            if (!state.TryRetain())
            {
                continue;
            }

            await state.Gate.WaitAsync(CancellationToken.None);

            IAsyncDisposable? subscription = null;
            bool releasePinnedBinding;
            var downstreamServer = state.DownstreamServer;

            try
            {
                subscription = state.Subscription;
                releasePinnedBinding = state.HasPinnedBinding;
                state.Subscription = null;
                state.HasPinnedBinding = false;
                state.ActiveAttempt = null;
                _ = state.MarkRetired();
            }
            finally
            {
                state.Gate.Release();
                McpGatewayResourceSubscriptionRegistry.Release(state);
            }

            await cleanup.DisposeAsync(
                subscription,
                releasePinnedBinding,
                downstreamServer,
                cleanupExceptions
            );
        }

        McpGatewayResourceSubscriptionCleanup.ThrowIfFailed(cleanupExceptions);
    }
}
