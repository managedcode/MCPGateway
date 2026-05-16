#pragma warning disable MCPEXP001

using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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

    [Test]
    public async Task CreateTaskAsync_UsesConfiguredTaskStoreMaximumTaskLimit()
    {
        var taskStore = CreateTaskStore(static options =>
        {
            options.McpTaskStore.MaximumTasks = 1;
            options.McpTaskStore.MaximumTasksPerSession = null;
        });
        var requestId = new RequestId("request-1");
        _ = await taskStore.CreateTaskAsync(
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

        Exception? exception = null;
        try
        {
            _ = await taskStore.CreateTaskAsync(
                new McpTaskMetadata(),
                new RequestId("request-2"),
                new JsonRpcRequest
                {
                    Id = new RequestId("request-2"),
                    Method = RequestMethods.ToolsCall,
                },
                "session-b",
                CancellationToken.None
            );
        }
        catch (Exception ex)
        {
            exception = ex;
        }

        await Assert.That(exception).IsTypeOf<InvalidOperationException>();
    }

    [Test]
    public async Task Constructor_RejectsUnboundedInvalidTaskStoreOptions()
    {
        Exception? exception = null;
        try
        {
            _ = CreateTaskStore(static options =>
            {
                options.McpTaskStore.TaskTimeToLive = TimeSpan.FromMinutes(2);
                options.McpTaskStore.MaximumTaskTimeToLive = TimeSpan.FromMinutes(1);
            });
        }
        catch (Exception ex)
        {
            exception = ex;
        }

        await Assert.That(exception).IsTypeOf<ArgumentOutOfRangeException>();
    }

    private static McpGatewayMcpServerTaskStore CreateTaskStore(
        Action<McpGatewayOptions>? configure = null
    )
    {
        var loggerFactory = NullLoggerFactory.Instance;
        var options = new McpGatewayOptions();
        configure?.Invoke(options);
        return new McpGatewayMcpServerTaskStore(
            new McpGatewayMcpServerBindingManager(new ThrowingBindingResolver()),
            new McpGatewayMcpServerRequestResolver(loggerFactory),
            EmptyServiceProvider.Instance,
            Options.Create(options),
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
