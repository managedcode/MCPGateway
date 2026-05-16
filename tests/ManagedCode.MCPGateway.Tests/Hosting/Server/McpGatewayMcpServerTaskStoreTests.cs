#pragma warning disable MCPEXP001

using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayMcpServerTaskStoreTests
{
    [Test]
    public async Task GetTaskResultAsync_ThrowsImmediatelyWhenTerminalTaskHasNoStoredResult()
    {
        var taskStore = CreateTaskStore();
        var requestId = new RequestId("request-1");
        var task = await taskStore.CreateTaskAsync(
            new McpTaskMetadata(),
            requestId,
            new JsonRpcRequest
            {
                Id = requestId,
                Method = RequestMethods.ToolsCall,
            },
            "session-a",
            CancellationToken.None
        );
        await taskStore.UpdateTaskStatusAsync(
            task.TaskId,
            McpTaskStatus.Completed,
            "done",
            "session-a",
            CancellationToken.None
        );

        Exception? exception = null;
        try
        {
            using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            _ = await taskStore.GetTaskResultAsync(
                task.TaskId,
                "session-a",
                cancellationSource.Token
            );
        }
        catch (Exception ex)
        {
            exception = ex;
        }

        await Assert.That(exception).IsTypeOf<InvalidOperationException>();
        await Assert.That(exception!.Message).Contains(task.TaskId);
    }

    private static McpGatewayMcpServerTaskStore CreateTaskStore()
    {
        var loggerFactory = NullLoggerFactory.Instance;
        return new McpGatewayMcpServerTaskStore(
            new McpGatewayMcpServerBindingManager(new ThrowingBindingResolver()),
            new McpGatewayMcpServerRequestResolver(loggerFactory),
            EmptyServiceProvider.Instance,
            loggerFactory.CreateLogger<McpGatewayMcpServerTaskStore>(),
            loggerFactory
        );
    }

    private sealed class ThrowingBindingResolver : IMcpGatewayServerBindingResolver
    {
        public ValueTask<IMcpGatewayServerBinding> ResolveAsync(
            IServiceProvider? requestServices,
            IServiceProvider serverServices,
            ModelContextProtocol.Server.McpServer server,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();
    }
}

#pragma warning restore MCPEXP001
