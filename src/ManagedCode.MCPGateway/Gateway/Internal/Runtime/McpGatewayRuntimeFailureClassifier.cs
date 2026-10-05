using ManagedCode.Communication.CQRS;

namespace ManagedCode.MCPGateway;

internal static class McpGatewayRuntimeFailureClassifier
{
    public static bool IsRecoverable(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception is not OperationCanceledException && !HasFatalFailure(exception);
    }

    public static bool HasFatalFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return CqrsRuntimeFailures.FindFatal(exception) is not null;
    }
}
