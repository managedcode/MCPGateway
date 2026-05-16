using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayMcpServerSessionCleaner(
    McpGatewayPromptListNotificationManager promptListNotificationManager,
    McpGatewayResourceSubscriptionManager resourceSubscriptionManager,
    McpGatewayMcpServerTaskStore taskStore,
    ILogger<McpGatewayMcpServerSessionCleaner> logger
)
{
    public async ValueTask RemoveSessionAsync(McpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        var gatewaySessionKey = McpGatewayMcpServerIdentity.GetKey(server);
        var mcpSessionId = server.SessionId ?? string.Empty;
        var cleanupExceptions = new List<Exception>();

        await RemoveSessionStateAsync(
            "prompt list notification",
            gatewaySessionKey,
            () => promptListNotificationManager.RemoveSessionAsync(gatewaySessionKey),
            cleanupExceptions
        );
        await RemoveSessionStateAsync(
            "resource subscription",
            mcpSessionId,
            () => resourceSubscriptionManager.RemoveSessionAsync(mcpSessionId),
            cleanupExceptions
        );
        await RemoveSessionStateAsync(
            "task binding",
            mcpSessionId,
            () => taskStore.RemoveSessionAsync(mcpSessionId),
            cleanupExceptions
        );

        ThrowIfCleanupFailed(cleanupExceptions);
    }

    private async ValueTask RemoveSessionStateAsync(
        string stateName,
        string sessionId,
        Func<ValueTask> cleanup,
        List<Exception> cleanupExceptions
    )
    {
        try
        {
            await cleanup();
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed to clean up MCP gateway {StateName} state for session '{SessionId}'.",
                stateName,
                sessionId
            );
            cleanupExceptions.Add(exception);
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
}
