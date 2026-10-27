#pragma warning disable MCPEXP003

using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Apps;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayMcpServerTaskFeatureIntegrationTests
{
    private const string LocalTaskSource = "local";
    private const string CallerStoreOutput = "Caller-owned persisted result.";
    private const string CallerStoreDescription = "Returns a caller-owned task result.";
    private const string LocalTaskToolName = "local_task_tool";
    private const string LocalCancellableTaskToolName = "local_cancellable_task_tool";
    private const string LocalFailingTaskToolName = "local_failing_task_tool";

    [Test]
    public async Task ListToolsAsync_AdvertisesCurrentTasksAndAppsExtensions()
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

        await Assert
            .That(gatewayServer.Client.ServerCapabilities.Extensions)
            .ContainsKey(TasksProtocol.ExtensionId);
        await Assert
            .That(gatewayServer.Client.ServerCapabilities.Extensions)
            .ContainsKey(McpApps.ExtensionId);
        await Assert
            .That(gatewayServer.Client.NegotiatedProtocolVersion)
            .IsEqualTo(TestMcpProtocolVersions.Current);
        await Assert
            .That(tools.Select(static tool => tool.Name))
            .Contains(TestMcpTaskFeatureServerHost.RequiredToolName);
        await Assert.That(tools.Select(static tool => tool.Name)).Contains(LocalTaskToolName);
    }

    [Test]
    public async Task WithMcpGatewayCatalog_UsesCallerProvidedTaskStore()
    {
        using var scope = new GatewayTaskDurabilityTestScope();
        using var taskStore = new FileBackedGatewayTaskStore(scope.DirectoryPath, TimeSpan.Zero);
        await using var host = await GatewayMcpServerHost.StartWithTaskStoreAsync(options => options.AddTool(
            LocalTaskSource, TestFunctionFactory.CreateFunction(() => CallerStoreOutput, LocalTaskToolName, CallerStoreDescription)),
            taskStore, cancellationToken: scope.Token);
        var started = await host.Client.CallToolAsTaskAsync(new CallToolRequestParams { Name = LocalTaskToolName }, scope.Token);
        await Assert.That(started.IsTask).IsTrue();
        var id = started.TaskCreated!.TaskId;
        var completed = await GatewayTaskDurabilityTestScope.WaitForTerminalAsync(host.Client, id, scope.Token);
        await Assert.That(completed).IsTypeOf<CompletedTaskResult>();
        await Assert.That(GetSingleText(GatewayTaskDurabilityTestScope.ToolResult((CompletedTaskResult)completed))).IsEqualTo(CallerStoreOutput);
        await Assert.That((await taskStore.GetTaskAsync(id, scope.Token))!.Status).IsEqualTo(McpTaskStatus.Completed);
        await Assert.That(await taskStore.GetTaskOwnerAsync(id, scope.Token)).IsNotNull();
    }

    [Test]
    public async Task CallToolAsync_RetriesAnUpstreamRequiredTaskAndReturnsItsResult()
    {
        await using var upstreamServer = await TestMcpTaskFeatureServerHost.StartAsync();
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(options =>
            options.AddMcpClient("source-a", upstreamServer.Client, disposeClient: false)
        );

        var result = await gatewayServer.Client.CallToolAsync(
            TestMcpTaskFeatureServerHost.RequiredToolName,
            new Dictionary<string, object?> { ["value"] = "alpha" }
        );

        await Assert.That(result.IsError == true).IsFalse();
        await Assert.That(GetSingleText(result)).IsEqualTo("required:alpha");
    }

    [Test]
    public async Task CallToolAsTaskAsync_CompletesAnUpstreamRequiredToolThroughGatewayOwnedTaskState()
    {
        await using var upstreamServer = await TestMcpTaskFeatureServerHost.StartAsync();
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(options =>
            options.AddMcpClient("source-a", upstreamServer.Client, disposeClient: false)
        );

        var created = await StartTaskAsync(
            gatewayServer.Client,
            TestMcpTaskFeatureServerHost.RequiredToolName,
            "beta"
        );
        var completed = await WaitForTaskAsync<CompletedTaskResult>(
            gatewayServer.Client,
            created.TaskId
        );
        var result = DeserializeToolResult(completed.Result);

        await Assert.That(GetSingleText(result)).IsEqualTo("required:beta");
    }

    [Test]
    public async Task CallToolWithPollingAsync_ReturnsTheCompletedLocalTaskResult()
    {
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(options =>
            options.AddTool(
                "local",
                TestFunctionFactory.CreateFunction(
                    (string value, CancellationToken cancellationToken) =>
                        RunLocalTaskToolAsync(value, cancellationToken),
                    LocalTaskToolName,
                    "Runs a local task-capable tool."
                )
            )
        );

        var result = await gatewayServer.Client.CallToolWithPollingAsync(
            CreateTaskRequest(LocalTaskToolName, "gamma")
        );

        await Assert.That(GetSingleText(result)).IsEqualTo("local:gamma");
    }

    [Test]
    public async Task CancelTaskAsync_CancelsAGatewayManagedLocalTask()
    {
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(options =>
            options.AddTool(
                "local",
                TestFunctionFactory.CreateFunction(
                    (string value, CancellationToken cancellationToken) =>
                        RunLocalCancellableTaskToolAsync(value, cancellationToken),
                    LocalCancellableTaskToolName,
                    "Runs a local cancellable task."
                )
            )
        );

        var created = await StartTaskAsync(
            gatewayServer.Client,
            LocalCancellableTaskToolName,
            "delta"
        );
        _ = await gatewayServer.Client.CancelTaskAsync(created.TaskId);
        var cancelled = await WaitForTaskAsync<CancelledTaskResult>(
            gatewayServer.Client,
            created.TaskId
        );

        await Assert.That(cancelled.Status).IsEqualTo(McpTaskStatus.Cancelled);
    }

    [Test]
    public async Task CallToolAsTaskAsync_StoresToolFailuresAsCompletedErrorResults()
    {
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(options =>
            options.AddTool(
                "local",
                TestFunctionFactory.CreateFunction(
                    static (string value) => ThrowForTaskCoverage(value),
                    LocalFailingTaskToolName,
                    "Runs a local task that fails."
                )
            )
        );

        var created = await StartTaskAsync(
            gatewayServer.Client,
            LocalFailingTaskToolName,
            "epsilon"
        );
        var completed = await WaitForTaskAsync<CompletedTaskResult>(
            gatewayServer.Client,
            created.TaskId
        );
        var result = DeserializeToolResult(completed.Result);

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(GetSingleText(result)).IsEqualTo("Tool invocation failed.");
    }

    private static async Task<CreateTaskResult> StartTaskAsync(
        McpClient client,
        string toolName,
        string value
    )
    {
        var result = await client.CallToolAsTaskAsync(CreateTaskRequest(toolName, value));
        if (!result.IsTask || result.TaskCreated is null)
        {
            throw new InvalidOperationException($"Tool '{toolName}' did not create an MCP task.");
        }

        return result.TaskCreated;
    }

    private static CallToolRequestParams CreateTaskRequest(string toolName, string value) =>
        new()
        {
            Name = toolName,
            Arguments = new Dictionary<string, JsonElement>
            {
                ["value"] = JsonSerializer.SerializeToElement(value),
            },
        };

    private static async Task<TTask> WaitForTaskAsync<TTask>(McpClient client, string taskId)
        where TTask : GetTaskResult
    {
        const int maximumAttempts = 200;
        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            var task = await client.GetTaskAsync(taskId);
            if (task is TTask expected)
            {
                return expected;
            }

            if (task is FailedTaskResult or CancelledTaskResult)
            {
                throw new InvalidOperationException(
                    $"Task '{taskId}' reached unexpected status '{task.Status}'."
                );
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }

        throw new InvalidOperationException(
            $"Task '{taskId}' did not reach '{typeof(TTask).Name}' in time."
        );
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
        result.Deserialize<CallToolResult>(McpGatewayJsonSerializer.Options)
        ?? throw new InvalidOperationException("Task result payload was not a CallToolResult.");

    private static string GetSingleText(CallToolResult result) =>
        ((TextContentBlock)result.Content.Single()).Text;
}

#pragma warning restore MCPEXP003
