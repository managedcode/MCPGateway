#pragma warning disable MCPEXP001

using System.Text.Json;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayMcpServerTaskFeatureIntegrationTests
{
    private const string LocalTaskToolName = "local_task_tool";
    private const string LocalCancellableTaskToolName = "local_cancellable_task_tool";
    private const string LocalFailingTaskToolName = "local_failing_task_tool";
    private const string InstantCompletedTaskToolName = "instant_completed_task_tool";

    [Test]
    public async Task ListToolsAsync_ExportsTaskSupportForUpstreamAndLocalTools()
    {
        await using var upstreamServer = await TestMcpTaskFeatureServerHost.StartAsync();
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(options =>
        {
            options.AddMcpClient("source-a", upstreamServer.Client, disposeClient: false);
            options.AddTool(
                "local",
                TestFunctionFactory.CreateFunction(
                    (string value, CancellationToken cancellationToken) =>
                        RunLocalTaskToolAsync(value, cancellationToken),
                    LocalTaskToolName,
                    "Runs a local task-capable tool."
                )
            );
        });

        var tools = await gatewayServer.Client.ListToolsAsync();
        var requiredTool = tools.Single(tool => tool.Name == $"source-a:{TestMcpTaskFeatureServerHost.RequiredToolName}");
        var optionalTool = tools.Single(tool => tool.Name == $"source-a:{TestMcpTaskFeatureServerHost.OptionalToolName}");
        var localTool = tools.Single(tool => tool.Name == $"local:{LocalTaskToolName}");

        await Assert.That(gatewayServer.Client.ServerCapabilities.Tasks).IsNotNull();
        await Assert.That(gatewayServer.Client.ServerCapabilities.Tasks?.Requests?.Tools?.Call).IsNotNull();
        await Assert.That(requiredTool.ProtocolTool.Execution?.TaskSupport).IsEqualTo(ToolTaskSupport.Required);
        await Assert.That(optionalTool.ProtocolTool.Execution?.TaskSupport).IsEqualTo(ToolTaskSupport.Optional);
        await Assert.That(localTool.ProtocolTool.Execution?.TaskSupport).IsEqualTo(ToolTaskSupport.Optional);
    }

    [Test]
    public async Task CallToolAsTaskAsync_ProxiesUpstreamRequiredToolAndSupportsListGetAndResult()
    {
        await using var upstreamServer = await TestMcpTaskFeatureServerHost.StartAsync();
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(options =>
        {
            options.AddMcpClient("source-a", upstreamServer.Client, disposeClient: false);
        });

        var task = await gatewayServer.Client.CallToolAsTaskAsync(
            $"source-a:{TestMcpTaskFeatureServerHost.RequiredToolName}",
            new Dictionary<string, object?> { ["value"] = "alpha" }
        );

        var listedTasks = await gatewayServer.Client.ListTasksAsync();
        var listedTask = listedTasks.Single(candidate => candidate.TaskId == task.TaskId);
        var trackedTask = await gatewayServer.Client.GetTaskAsync(task.TaskId);
        var taskResult = DeserializeToolResult(
            await gatewayServer.Client.GetTaskResultAsync(task.TaskId)
        );
        var completedTask = await gatewayServer.Client.GetTaskAsync(task.TaskId);

        await Assert.That(task.TaskId).IsNotEmpty();
        await Assert.That(listedTask.TaskId).IsEqualTo(task.TaskId);
        await Assert.That(trackedTask).IsNotNull();
        await Assert.That(completedTask?.Status).IsEqualTo(McpTaskStatus.Completed);
        await Assert.That(GetSingleText(taskResult)).IsEqualTo("required:alpha");
    }

    [Test]
    public async Task CallToolAsync_WhenToolRequiresTask_ReturnsTaskRequirementError()
    {
        await using var upstreamServer = await TestMcpTaskFeatureServerHost.StartAsync();
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(options =>
        {
            options.AddMcpClient("source-a", upstreamServer.Client, disposeClient: false);
        });

        var result = await gatewayServer.Client.CallToolAsync(
            $"source-a:{TestMcpTaskFeatureServerHost.RequiredToolName}",
            new Dictionary<string, object?> { ["value"] = "alpha" }
        );

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(GetSingleText(result)).Contains("requires task augmentation");
    }

    [Test]
    public async Task TaskStatusNotification_IsForwardedForUpstreamTask()
    {
        await using var upstreamServer = await TestMcpTaskFeatureServerHost.StartAsync();
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(options =>
        {
            options.AddMcpClient("source-a", upstreamServer.Client, disposeClient: false);
        });

        var completionNotification = new TaskCompletionSource<McpTaskStatusNotificationParams>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        await using var notificationRegistration = gatewayServer.Client.RegisterNotificationHandler(
            NotificationMethods.TaskStatusNotification,
            (notification, _) =>
            {
                var payload = notification.Params?.Deserialize<McpTaskStatusNotificationParams>();
                if (payload?.Status == McpTaskStatus.Completed)
                {
                    completionNotification.TrySetResult(payload);
                }

                return ValueTask.CompletedTask;
            }
        );

        var task = await gatewayServer.Client.CallToolAsTaskAsync(
            $"source-a:{TestMcpTaskFeatureServerHost.OptionalToolName}",
            new Dictionary<string, object?> { ["value"] = "beta" }
        );

        var payload = await completionNotification.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(payload.TaskId).IsEqualTo(task.TaskId);
        await Assert.That(payload.Status).IsEqualTo(McpTaskStatus.Completed);
    }

    [Test]
    public async Task TaskStatusNotification_CatchesUpWhenUpstreamTaskCompletesBeforeSubscription()
    {
        var source = new CompletedBeforeSubscriptionTaskSource("race-source");
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(
            static _ => { },
            services =>
                services.AddSingleton<IMcpGatewayServerBindingResolver>(serviceProvider =>
                    new SingleSourceTaskBindingResolver(
                        serviceProvider.GetRequiredService<IMcpGatewayFactory>(),
                        source,
                        InstantCompletedTaskToolName
                    )
                )
        );
        var completionNotification = new TaskCompletionSource<McpTaskStatusNotificationParams>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        await using var notificationRegistration = gatewayServer.Client.RegisterNotificationHandler(
            NotificationMethods.TaskStatusNotification,
            (notification, _) =>
            {
                var payload = notification.Params?.Deserialize<McpTaskStatusNotificationParams>();
                if (payload?.Status == McpTaskStatus.Completed)
                {
                    completionNotification.TrySetResult(payload);
                }

                return ValueTask.CompletedTask;
            }
        );

        var task = await gatewayServer.Client.CallToolAsTaskAsync(
            $"{source.SourceId}:{InstantCompletedTaskToolName}",
            new Dictionary<string, object?> { ["value"] = "race" }
        );
        var payload = await completionNotification.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(source.SubscribeCount).IsEqualTo(1);
        await Assert.That(payload.TaskId).IsEqualTo(task.TaskId);
        await Assert.That(payload.Status).IsEqualTo(McpTaskStatus.Completed);
    }

    [Test]
    public async Task CallToolAsTaskAsync_CancelsGatewayManagedLocalTask()
    {
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(options =>
        {
            options.AddTool(
                "local",
                TestFunctionFactory.CreateFunction(
                    (string value, CancellationToken cancellationToken) =>
                        RunLocalCancellableTaskToolAsync(value, cancellationToken),
                    LocalCancellableTaskToolName,
                    "Runs a local cancellable task."
                )
            );
        });

        var task = await gatewayServer.Client.CallToolAsTaskAsync(
            $"local:{LocalCancellableTaskToolName}",
            new Dictionary<string, object?> { ["value"] = "gamma" }
        );

        var cancelledTask = await gatewayServer.Client.CancelTaskAsync(task.TaskId);
        var trackedTask = await gatewayServer.Client.GetTaskAsync(task.TaskId);

        await Assert.That(cancelledTask.Status).IsEqualTo(McpTaskStatus.Cancelled);
        await Assert.That(trackedTask?.Status).IsEqualTo(McpTaskStatus.Cancelled);
    }

    [Test]
    public async Task CallToolAsTaskAsync_WaitsForGatewayManagedLocalTaskResult()
    {
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(options =>
        {
            options.AddTool(
                "local",
                TestFunctionFactory.CreateFunction(
                    (string value, CancellationToken cancellationToken) =>
                        RunLocalTaskToolAsync(value, cancellationToken),
                    LocalTaskToolName,
                    "Runs a local task-capable tool."
                )
            );
        });

        var task = await gatewayServer.Client.CallToolAsTaskAsync(
            $"local:{LocalTaskToolName}",
            new Dictionary<string, object?> { ["value"] = "delta" }
        );
        var taskResult = DeserializeToolResult(
            await gatewayServer.Client.GetTaskResultAsync(task.TaskId)
        );
        var trackedTask = await gatewayServer.Client.GetTaskAsync(task.TaskId);

        await Assert.That(trackedTask?.Status).IsEqualTo(McpTaskStatus.Completed);
        await Assert.That(GetSingleText(taskResult)).IsEqualTo("local:delta");
    }

    [Test]
    public async Task CallToolAsTaskAsync_StoresFailedResultForGatewayManagedLocalTask()
    {
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(options =>
        {
            options.AddTool(
                "local",
                TestFunctionFactory.CreateFunction(
                    static (string value) => ThrowForTaskCoverage(value),
                    LocalFailingTaskToolName,
                    "Runs a local task that fails."
                )
            );
        });

        var task = await gatewayServer.Client.CallToolAsTaskAsync(
            $"local:{LocalFailingTaskToolName}",
            new Dictionary<string, object?> { ["value"] = "epsilon" }
        );
        var taskResult = DeserializeToolResult(
            await gatewayServer.Client.GetTaskResultAsync(task.TaskId)
        );
        var trackedTask = await gatewayServer.Client.GetTaskAsync(task.TaskId);

        await Assert.That(trackedTask?.Status).IsEqualTo(McpTaskStatus.Completed);
        await Assert.That(taskResult.IsError).IsTrue();
        await Assert.That(GetSingleText(taskResult)).Contains("boom:epsilon");
    }

    [Test]
    public async Task CallToolAsTaskAsync_CancelsUpstreamProxyTask()
    {
        await using var upstreamServer = await TestMcpTaskFeatureServerHost.StartAsync();
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(options =>
        {
            options.AddMcpClient("source-a", upstreamServer.Client, disposeClient: false);
        });

        var task = await gatewayServer.Client.CallToolAsTaskAsync(
            $"source-a:{TestMcpTaskFeatureServerHost.CancellableToolName}",
            new Dictionary<string, object?> { ["value"] = "zeta" }
        );

        var cancelledTask = await gatewayServer.Client.CancelTaskAsync(task.TaskId);
        var trackedTask = await gatewayServer.Client.GetTaskAsync(task.TaskId);

        await Assert.That(cancelledTask.Status).IsEqualTo(McpTaskStatus.Cancelled);
        await Assert.That(trackedTask?.Status).IsEqualTo(McpTaskStatus.Cancelled);
    }

    private static async Task<string> RunLocalTaskToolAsync(
        string value,
        CancellationToken cancellationToken
    )
    {
        await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        return $"local:{value}";
    }

    private static async Task<string> RunLocalCancellableTaskToolAsync(
        string value,
        CancellationToken cancellationToken
    )
    {
        await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
        return $"local-cancellable:{value}";
    }

    private static string ThrowForTaskCoverage(string value) =>
        throw new InvalidOperationException($"boom:{value}");

    private static CallToolResult DeserializeToolResult(JsonElement result) =>
        result.Deserialize<CallToolResult>(McpJsonUtilities.DefaultOptions)
        ?? throw new InvalidOperationException("Task result payload was not a CallToolResult.");

    private static string GetSingleText(CallToolResult result) =>
        ((TextContentBlock)result.Content.Single()).Text;
}

#pragma warning restore MCPEXP001
