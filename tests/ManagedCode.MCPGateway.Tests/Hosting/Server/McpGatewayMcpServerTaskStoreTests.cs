using System.Text.Json;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayMcpServerTaskStoreTests
{
    [Test]
    public async Task CreateTaskAsync_UsesConfiguredRetentionAndPollInterval()
    {
        var store = CreateStore(options =>
        {
            options.TaskTimeToLive = TimeSpan.FromMinutes(2);
            options.PollInterval = TimeSpan.FromMilliseconds(125);
        });

        var task = await store.CreateTaskAsync();

        await Assert.That(task.Status).IsEqualTo(McpTaskStatus.Working);
        await Assert.That(task.TimeToLive).IsEqualTo(TimeSpan.FromMinutes(2));
        await Assert.That(task.PollIntervalMs).IsEqualTo(125);
        await Assert.That(store.Count).IsEqualTo(1);
    }

    [Test]
    public async Task CreateTaskAsync_EnforcesTheConfiguredGlobalLimit()
    {
        var store = CreateStore(options => options.MaximumTasks = 1);
        _ = await store.CreateTaskAsync();

        var exception = await CaptureAsync(() => store.CreateTaskAsync());

        await Assert.That(exception).IsTypeOf<InvalidOperationException>();
        await Assert.That(exception!.Message).Contains("limit of 1");
    }

    [Test]
    public async Task SetCompletedAsync_PreservesTheTypedJsonResult()
    {
        var store = CreateStore();
        var task = await store.CreateTaskAsync();
        var expected = JsonSerializer.SerializeToElement(new { value = "done" });

        await store.SetCompletedAsync(task.TaskId, expected);
        var completed = await store.GetTaskAsync(task.TaskId);

        await Assert.That(completed).IsNotNull();
        await Assert.That(completed!.Status).IsEqualTo(McpTaskStatus.Completed);
        await Assert.That(completed.Result?.GetProperty("value").GetString()).IsEqualTo("done");
    }

    [Test]
    public async Task ResolveInputRequestsAsync_RaisesTheV2ResponseEventAndReturnsToWorking()
    {
        var store = CreateStore();
        var task = await store.CreateTaskAsync();
        InputResponseReceivedEventArgs? received = null;
        store.InputResponseReceived += args => received = args;
        await store.SetInputRequestsAsync(
            task.TaskId,
            new Dictionary<string, InputRequest>
            {
                ["request-1"] = new InputRequest
                {
                    Method = "elicitation/create",
                    Params = JsonSerializer.SerializeToElement(new { message = "Confirm" }),
                },
            }
        );

        await store.ResolveInputRequestsAsync(
            task.TaskId,
            new Dictionary<string, InputResponse>
            {
                ["request-1"] = new InputResponse
                {
                    RawValue = JsonSerializer.SerializeToElement(new { action = "accept" }),
                },
            }
        );
        var updated = await store.GetTaskAsync(task.TaskId);

        await Assert.That(received).IsNotNull();
        await Assert.That(received!.TaskId).IsEqualTo(task.TaskId);
        await Assert.That(received.RequestId).IsEqualTo("request-1");
        await Assert.That(updated!.Status).IsEqualTo(McpTaskStatus.Working);
        await Assert.That(updated.InputRequests?.Count ?? 0).IsEqualTo(0);
    }

    [Test]
    public async Task CreateTaskAsync_ReclaimsExpiredEntriesBeforeApplyingTheLimit()
    {
        var store = CreateStore(options =>
        {
            options.MaximumTasks = 1;
            options.TaskTimeToLive = TimeSpan.FromMilliseconds(20);
        });
        var expired = await store.CreateTaskAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(50));

        var replacement = await store.CreateTaskAsync();

        await Assert.That(replacement.TaskId).IsNotEqualTo(expired.TaskId);
        await Assert.That(await store.GetTaskAsync(expired.TaskId)).IsNull();
        await Assert.That(store.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Configure_RejectsUnboundedOrNonPositiveOptions()
    {
        var store = new McpGatewayMcpServerTaskStore();
        var options = new McpGatewayMcpTaskStoreOptions { TaskTimeToLive = TimeSpan.Zero };

        var exception = await CaptureAsync(() =>
        {
            store.Configure(options);
            return Task.CompletedTask;
        });

        await Assert.That(exception).IsTypeOf<ArgumentOutOfRangeException>();
    }

    private static McpGatewayMcpServerTaskStore CreateStore(
        Action<McpGatewayMcpTaskStoreOptions>? configure = null
    )
    {
        var options = new McpGatewayMcpTaskStoreOptions();
        configure?.Invoke(options);
        var store = new McpGatewayMcpServerTaskStore();
        store.Configure(options);
        return store;
    }

    private static async Task<Exception?> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }
}
