using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayRecoveredTaskCancellationTests
{
    private const string Source = "durable";
    private const string ToolName = "recoverable_cancel";
    private const string ApprovalId = "approval";
    private const string ApprovalMethod = "elicitation/create";
    private const string ApprovalMessage = "Confirm the pending operation.";
    private const string ResultText = "Late executor completion.";
    private const string Subject = "caller-a";
    private const string Tenant = "tenant-a";
    private const string ForeignTenant = "tenant-b";
    private const string Partition = "partition-a";
    private const string Revision = "policy-v1";
    private const int NoCalls = 0;
    private static readonly TimeSpan HistoricalAge = TimeSpan.FromHours(2);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NewSdkHost_CancelsPersistedTask_DeniesForeignTenant_AndFencesLateCompletion(bool inputRequired)
    {
        using var scope = new GatewayTaskDurabilityTestScope();
        using var originalStore = new FileBackedGatewayTaskStore(scope.DirectoryPath, HistoricalAge);
        using var recoveredStore = new FileBackedGatewayTaskStore(scope.DirectoryPath, TimeSpan.Zero);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recoveredInvocations = NoCalls;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var original = await GatewayMcpServerHost.StartWithTaskStoreAsync(options => options.AddTool(Source,
            AIFunctionFactory.Create(async (CancellationToken cancellationToken) =>
            {
                if (inputRequired)
                {
                    var created = await originalStore.Created.Task.WaitAsync(cancellationToken);
                    await originalStore.SetInputRequestsAsync(created.TaskId, new Dictionary<string, InputRequest>
                    {
                        [ApprovalId] = new InputRequest
                        {
                            Method = ApprovalMethod,
                            Params = JsonSerializer.SerializeToElement(new { message = ApprovalMessage })
                        }
                    }, cancellationToken);
                }
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                return ResultText;
            }, new AIFunctionFactoryOptions { Name = ToolName })), originalStore,
            services => services.AddSingleton<IMcpGatewayExecutionContextProvider>(new Actor()), scope.Token);
        try
        {
            var accepted = await original.Client.CallToolAsTaskAsync(new CallToolRequestParams { Name = ToolName }, scope.Token);
            await Assert.That(accepted.IsTask).IsTrue();
            var taskId = accepted.TaskCreated!.TaskId;
            await entered.Task.WaitAsync(scope.Token);
            var actor = new Actor();
            await using var recovered = await GatewayMcpServerHost.StartWithTaskStoreAsync(options => options.AddTool(Source,
                AIFunctionFactory.Create(() => { Interlocked.Increment(ref recoveredInvocations); return ResultText; },
                    new AIFunctionFactoryOptions { Name = ToolName })), recoveredStore,
                services => services.AddSingleton<IMcpGatewayExecutionContextProvider>(actor), scope.Token);
            var initial = await recovered.Client.GetTaskAsync(taskId, scope.Token);
            if (inputRequired)
            {
                await Assert.That(initial).IsTypeOf<InputRequiredTaskResult>();
                await Assert.That(((InputRequiredTaskResult)initial).InputRequests).IsNotNull();
                await Assert.That(((InputRequiredTaskResult)initial).InputRequests!.Keys).Contains(ApprovalId);
            }
            else
            {
                await Assert.That(initial).IsTypeOf<WorkingTaskResult>();
            }
            actor.Current = actor.Current with { TenantId = ForeignTenant };
            await Assert.That(async () => await recovered.Client.CancelTaskAsync(taskId, scope.Token)).Throws<McpException>();
            await Assert.That((await originalStore.GetTaskAsync(taskId, scope.Token))!.Status)
                .IsEqualTo(inputRequired ? McpTaskStatus.InputRequired : McpTaskStatus.Working);
            actor.Current = actor.Current with { TenantId = Tenant };
            _ = await recovered.Client.CancelTaskAsync(taskId, scope.Token);
            await Assert.That(await recovered.Client.GetTaskAsync(taskId, scope.Token)).IsTypeOf<CancelledTaskResult>();
            await Assert.That((await originalStore.GetTaskAsync(taskId, scope.Token))!.Status).IsEqualTo(McpTaskStatus.Cancelled);
            release.TrySetResult();
            await originalStore.CompletionAttempted.Task.WaitAsync(scope.Token);
            await Assert.That((await recoveredStore.GetTaskAsync(taskId, scope.Token))!.Status).IsEqualTo(McpTaskStatus.Cancelled);
            await Assert.That(await recovered.Client.GetTaskAsync(taskId, scope.Token)).IsTypeOf<CancelledTaskResult>();
            await Assert.That(recoveredInvocations).IsEqualTo(NoCalls);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    private sealed class Actor : IMcpGatewayExecutionContextProvider
    {
        public McpGatewayExecutionContext Current { get; set; } = new(Subject, Tenant, Partition, Revision);
        public ValueTask<McpGatewayExecutionContext> GetCurrentAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Current);
    }
}
