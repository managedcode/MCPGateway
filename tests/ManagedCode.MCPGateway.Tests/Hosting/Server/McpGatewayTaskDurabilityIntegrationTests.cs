using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayTaskDurabilityIntegrationTests
{
    private const string SourceId = "durable";
    private const string ResultToolName = "durable_result";
    private const string ResultDescription = "Returns one persisted terminal result.";
    private const string PersistedOutput = "Persisted terminal output.";
    private const string ActiveToolName = "active_retention";
    private const string QuickToolName = "capacity_probe";
    private const string OriginalCaller = "caller-a";
    private const string ForeignCaller = "caller-b";
    private const string Tenant = "tenant-a";
    private const string AuthorizationPartition = "partition-a";
    private const string PolicyRevision = "policy-v1";
    private const int NoInvocations = 0;
    private const int OneInvocation = 1;
    private static readonly TimeSpan HistoricalTaskAge = TimeSpan.FromHours(2);
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ActiveElapsed = TimeSpan.FromDays(2);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan ExpiryBoundary = TimeSpan.FromTicks(1);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TerminalResultAndOwner_AreRecoveredByFreshSdkHostAndFileStore(bool toolError)
    {
        using var scope = new GatewayTaskDurabilityTestScope();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string taskId;
        using (var initialStore = new FileBackedGatewayTaskStore(scope.DirectoryPath, HistoricalTaskAge))
        {
            await using var initial = await GatewayMcpServerHost.StartWithTaskStoreAsync(options => options.AddTool(SourceId,
                AIFunctionFactory.Create(async (CancellationToken cancellationToken) =>
                {
                    await release.Task.WaitAsync(cancellationToken);
                    return new CallToolResult { IsError = toolError, Content = [new TextContentBlock { Text = PersistedOutput }] };
                }, new AIFunctionFactoryOptions
                {
                    Name = ResultToolName,
                    Description = ResultDescription,
                    ExcludeResultSchema = true,
                    MarshalResult = static (value, _, _) => ValueTask.FromResult(value)
                })),
                initialStore, services => services.AddSingleton<IMcpGatewayExecutionContextProvider>(new Actor()), scope.Token);
            try
            {
                var created = await initial.Client.CallToolAsTaskAsync(new CallToolRequestParams { Name = ResultToolName }, scope.Token);
                await Assert.That(created.IsTask).IsTrue();
                taskId = created.TaskCreated!.TaskId;
                await Assert.That(await initial.Client.GetTaskAsync(taskId, scope.Token)).IsTypeOf<WorkingTaskResult>();
                release.TrySetResult();
                var completed = await GatewayTaskDurabilityTestScope.WaitForTerminalAsync(initial.Client, taskId, scope.Token);
                await Assert.That(completed).IsTypeOf<CompletedTaskResult>();
            }
            finally
            {
                release.TrySetResult();
            }
        }

        var resumedInvocations = NoInvocations;
        var actor = new Actor();
        var policy = new Policy();
        using var recoveredStore = new FileBackedGatewayTaskStore(scope.DirectoryPath, TimeSpan.Zero);
        await using var recovered = await GatewayMcpServerHost.StartWithTaskStoreAsync(options => options.AddTool(SourceId,
            AIFunctionFactory.Create(() => { Interlocked.Increment(ref resumedInvocations); return PersistedOutput; },
                new AIFunctionFactoryOptions
                {
                    Name = ResultToolName,
                    Description = ResultDescription,
                    ExcludeResultSchema = true,
                    MarshalResult = static (value, _, _) => ValueTask.FromResult(value)
                })), recoveredStore, services =>
            {
                services.AddSingleton<IMcpGatewayExecutionContextProvider>(actor);
                services.AddSingleton<IMcpGatewayAccessPolicy>(policy);
            }, scope.Token);
        var restored = await recovered.Client.GetTaskAsync(taskId, scope.Token);
        await Assert.That(restored).IsTypeOf<CompletedTaskResult>();
        var result = GatewayTaskDurabilityTestScope.ToolResult((CompletedTaskResult)restored);
        await Assert.That(((TextContentBlock)result.Content.Single()).Text).IsEqualTo(PersistedOutput);
        await Assert.That(result.IsError is true).IsEqualTo(toolError);
        await Assert.That(resumedInvocations).IsEqualTo(NoInvocations);

        actor.Current = actor.Current with { SubjectId = ForeignCaller };
        await Assert.That(async () => await recovered.Client.GetTaskAsync(taskId, scope.Token)).Throws<McpException>();
        await Assert.That(async () => await recovered.Client.CancelTaskAsync(taskId, scope.Token)).Throws<McpException>();
        actor.Current = actor.Current with { SubjectId = OriginalCaller };
        policy.AllowTaskAccess = false;
        await Assert.That(async () => await recovered.Client.GetTaskAsync(taskId, scope.Token)).Throws<McpException>();
        policy.AllowTaskAccess = true;
        _ = await recovered.Client.CancelTaskAsync(taskId, scope.Token);
        await Assert.That(await recovered.Client.GetTaskAsync(taskId, scope.Token)).IsTypeOf<CompletedTaskResult>();
        await Assert.That((await recoveredStore.GetTaskOwnerAsync(taskId, scope.Token))!.SubjectId).IsEqualTo(OriginalCaller);
        await Assert.That(resumedInvocations).IsEqualTo(NoInvocations);
    }

    [Test]
    public async Task ActiveTask_OutlivesRetention_ThenExpiresAfterCompletionAndReleasesCapacity()
    {
        using var scope = new GatewayTaskDurabilityTestScope();
        var clock = new GatewayTaskTestTimeProvider();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var quickInvocations = NoInvocations;
        await using var host = await GatewayMcpServerHost.StartAsync(options =>
        {
            options.McpTaskStore.CompletedTaskRetention = Retention;
            options.McpTaskStore.PollInterval = PollInterval;
            options.McpTaskStore.MaximumTasks = OneInvocation;
            options.AddTool(SourceId, AIFunctionFactory.Create(async (CancellationToken cancellationToken) =>
            {
                await release.Task.WaitAsync(cancellationToken);
                return PersistedOutput;
            }, new AIFunctionFactoryOptions { Name = ActiveToolName }));
            options.AddTool(SourceId, AIFunctionFactory.Create(() =>
            {
                Interlocked.Increment(ref quickInvocations);
                return PersistedOutput;
            }, new AIFunctionFactoryOptions { Name = QuickToolName }));
        }, services => services.AddSingleton<TimeProvider>(clock), scope.Token);
        try
        {
            var created = await host.Client.CallToolAsTaskAsync(new CallToolRequestParams { Name = ActiveToolName }, scope.Token);
            await Assert.That(created.IsTask).IsTrue();
            var taskId = created.TaskCreated!.TaskId;
            clock.Advance(ActiveElapsed);
            await Assert.That(await host.Client.GetTaskAsync(taskId, scope.Token)).IsTypeOf<WorkingTaskResult>();
            var refused = await host.Client.CallToolAsTaskAsync(new CallToolRequestParams { Name = QuickToolName }, scope.Token);
            await Assert.That(refused.IsTask).IsFalse();
            await Assert.That(refused.Result!.IsError).IsTrue();
            await Assert.That(await host.Client.GetTaskAsync(taskId, scope.Token)).IsTypeOf<WorkingTaskResult>();
            await Assert.That(quickInvocations).IsEqualTo(NoInvocations);
            release.TrySetResult();
            var completed = await GatewayTaskDurabilityTestScope.WaitForTerminalAsync(host.Client, taskId, scope.Token);
            await Assert.That(completed).IsTypeOf<CompletedTaskResult>();
            await Assert.That(((TextContentBlock)GatewayTaskDurabilityTestScope.ToolResult((CompletedTaskResult)completed).Content.Single()).Text)
                .IsEqualTo(PersistedOutput);
            clock.Advance(Retention - ExpiryBoundary);
            await Assert.That(await host.Client.GetTaskAsync(taskId, scope.Token)).IsTypeOf<CompletedTaskResult>();
            clock.Advance(ExpiryBoundary);
            await Assert.That(async () => await host.Client.GetTaskAsync(taskId, scope.Token)).Throws<McpException>();
            var replacement = await host.Client.CallToolAsTaskAsync(new CallToolRequestParams { Name = QuickToolName }, scope.Token);
            await Assert.That(replacement.IsTask).IsTrue();
            await Assert.That(await GatewayTaskDurabilityTestScope.WaitForTerminalAsync(host.Client, replacement.TaskCreated!.TaskId, scope.Token))
                .IsTypeOf<CompletedTaskResult>();
            await Assert.That(quickInvocations).IsEqualTo(OneInvocation);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    private sealed class Actor : IMcpGatewayExecutionContextProvider
    {
        public McpGatewayExecutionContext Current { get; set; } = new(OriginalCaller, Tenant, AuthorizationPartition, PolicyRevision);
        public ValueTask<McpGatewayExecutionContext> GetCurrentAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Current);
    }

    private sealed class Policy : IMcpGatewayAccessPolicy
    {
        public bool AllowTaskAccess { get; set; } = true;
        public ValueTask<McpGatewayAccessDecision> AuthorizeAsync(McpGatewayExecutionContext context, McpGatewayAccessRequest request,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(new McpGatewayAccessDecision(
                request.Operation != McpGatewayOperation.AccessTask || AllowTaskAccess));
    }
}
