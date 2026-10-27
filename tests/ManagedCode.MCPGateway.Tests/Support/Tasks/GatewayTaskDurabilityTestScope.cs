using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Tests;

internal sealed class GatewayTaskDurabilityTestScope : IDisposable
{
    private const string SolutionName = "ManagedCode.MCPGateway.slnx";
    private const string ArtifactsDirectory = "artifacts";
    private const string TaskDirectory = "task-durability";
    private const string IdFormat = "N";
    private const string RepositoryMissing = "The task test repository root was not found.";
    private const string InvalidResult = "The persisted task result is not a tool result.";
    private const long DefaultPollMilliseconds = 10;
    private static readonly TimeSpan TestBudget = TimeSpan.FromSeconds(30);
    private readonly CancellationTokenSource _deadline = new(TestBudget);

    public GatewayTaskDurabilityTestScope()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, SolutionName)))
        {
            root = root.Parent;
        }
        if (root is null)
        {
            throw new InvalidOperationException(RepositoryMissing);
        }
        DirectoryPath = Path.Combine(root.FullName, ArtifactsDirectory, TaskDirectory, Guid.CreateVersion7().ToString(IdFormat));
        Directory.CreateDirectory(DirectoryPath);
    }

    public string DirectoryPath { get; }
    public CancellationToken Token => _deadline.Token;

    public static async Task<GetTaskResult> WaitForTerminalAsync(McpClient client, string id, CancellationToken cancellationToken)
    {
        var state = await client.GetTaskAsync(id, cancellationToken);
        while (state is WorkingTaskResult)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(state.PollIntervalMs ?? DefaultPollMilliseconds), cancellationToken);
            state = await client.GetTaskAsync(id, cancellationToken);
        }
        return state;
    }

    public static CallToolResult ToolResult(CompletedTaskResult task) =>
        task.Result.Deserialize<CallToolResult>(McpGatewayJsonSerializer.Options) ?? throw new InvalidOperationException(InvalidResult);

    public void Dispose()
    {
        _deadline.Dispose();
        Directory.Delete(DirectoryPath, recursive: true);
    }
}

internal sealed class GatewayTaskTestTimeProvider : TimeProvider
{
    private long _ticks = TimeProvider.System.GetUtcNow().UtcTicks;
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
    public void Advance(TimeSpan duration) => Interlocked.Add(ref _ticks, duration.Ticks);
}
