using ManagedCode.MarkdownLd.Kb.Pipeline;

namespace ManagedCode.MCPGateway.Tests;

/// <summary>
/// Lifecycle contract of the content-addressed graph index pool over real Markdown-LD graph artifacts:
/// single-flight builds, failure recovery, joined cancellation, bounded idle retention and disposal.
/// </summary>
public sealed class McpGatewaySharedIndexPoolTests
{
    private const string FirstFingerprint = "first-catalog";
    private const string SecondFingerprint = "second-catalog";

    [TUnit.Core.Test]
    public async Task AcquireAsync_CoalescesWaitingAcquisitionsIntoOneBuild()
    {
        var pool = new McpGatewaySharedIndexPool<McpGatewayRuntime.ToolGraphArtifact>(maximumIdleIndexes: 1);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builds = 0;
        async Task<McpGatewayRuntime.ToolGraphArtifact> BuildAsync()
        {
            Interlocked.Increment(ref builds);
            await gate.Task;
            return await CreateArtifactAsync("coalesced");
        }

        var acquisitions = Enumerable.Range(0, 5)
            .Select(_ => pool.AcquireAsync(FirstFingerprint, BuildAsync, CancellationToken.None))
            .ToArray();
        await Assert.That(acquisitions.Any(static acquisition => acquisition.IsCompleted)).IsFalse();
        gate.SetResult();
        var leases = await Task.WhenAll(acquisitions);

        await Assert.That(builds).IsEqualTo(1);
        await Assert.That(pool.BuildCount).IsEqualTo(1);
        await Assert.That(leases.Count(static lease => !lease.IsReused)).IsEqualTo(1);
        await Assert.That(leases.All(lease => ReferenceEquals(lease.Index, leases[0].Index))).IsTrue();
        foreach (var lease in leases)
        {
            await lease.ReleaseAsync(keepForReuse: true);
        }

        await Assert.That(pool.IdleCount).IsEqualTo(1);
        await Assert.That(leases[0].Index.Graph.TripleCount).IsGreaterThan(0);
    }

    [TUnit.Core.Test]
    public async Task AcquireAsync_ForgetsAFailedBuildSoTheNextAcquisitionBuildsAgain()
    {
        var pool = new McpGatewaySharedIndexPool<McpGatewayRuntime.ToolGraphArtifact>(maximumIdleIndexes: 1);

        await Assert.ThrowsAsync<InvalidDataException>(async () => await pool.AcquireAsync(
            FirstFingerprint,
            static () => Task.FromException<McpGatewayRuntime.ToolGraphArtifact>(new InvalidDataException("Graph build failed.")),
            CancellationToken.None));
        await Assert.That(pool.Count).IsEqualTo(0);

        var lease = await pool.AcquireAsync(FirstFingerprint, static () => CreateArtifactAsync("retried"), CancellationToken.None);

        await Assert.That(lease.IsReused).IsFalse();
        await Assert.That(pool.BuildCount).IsEqualTo(2);
        await lease.ReleaseAsync(keepForReuse: false);
        await Assert.That(pool.Count).IsEqualTo(0);
    }

    [TUnit.Core.Test]
    public async Task AcquireAsync_CanceledLastWaiterJoinsTheBuildAndKeepsItsResultForReuse()
    {
        var pool = new McpGatewaySharedIndexPool<McpGatewayRuntime.ToolGraphArtifact>(maximumIdleIndexes: 1);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var acquisition = pool.AcquireAsync(
            FirstFingerprint,
            async () =>
            {
                await gate.Task;
                return await CreateArtifactAsync("joined");
            },
            cancellation.Token
        );

        await cancellation.CancelAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        await Assert.That(acquisition.IsCompleted).IsFalse();
        gate.SetResult();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await acquisition);
        await Assert.That(pool.IdleCount).IsEqualTo(1);
        var lease = await pool.AcquireAsync(FirstFingerprint, static () => CreateArtifactAsync("unused"), CancellationToken.None);
        await Assert.That(lease.IsReused).IsTrue();
        await Assert.That(pool.BuildCount).IsEqualTo(1);
        await lease.ReleaseAsync(keepForReuse: false);
    }

    [TUnit.Core.Test]
    public async Task ReleaseAsync_RetiresTheLeastRecentlyReleasedIdleIndexBeyondTheBoundAndDisposesIt()
    {
        var pool = new McpGatewaySharedIndexPool<McpGatewayRuntime.ToolGraphArtifact>(maximumIdleIndexes: 1);
        var first = await pool.AcquireAsync(FirstFingerprint, static () => CreateArtifactAsync("first"), CancellationToken.None);
        var second = await pool.AcquireAsync(SecondFingerprint, static () => CreateArtifactAsync("second"), CancellationToken.None);

        await first.ReleaseAsync(keepForReuse: true);
        await Assert.That(pool.IdleCount).IsEqualTo(1);
        await second.ReleaseAsync(keepForReuse: true);

        await Assert.That(pool.Count).IsEqualTo(1);
        await Assert.That(pool.IdleCount).IsEqualTo(1);
        Assert.Throws<ObjectDisposedException>(() => _ = first.Index.Graph.TripleCount);
        await Assert.That(second.Index.Graph.TripleCount).IsGreaterThan(0);

        var reused = await pool.AcquireAsync(SecondFingerprint, static () => CreateArtifactAsync("unused"), CancellationToken.None);
        var rebuilt = await pool.AcquireAsync(FirstFingerprint, static () => CreateArtifactAsync("first"), CancellationToken.None);

        await Assert.That(reused.IsReused).IsTrue();
        await Assert.That(ReferenceEquals(reused.Index, second.Index)).IsTrue();
        await Assert.That(rebuilt.IsReused).IsFalse();
        await Assert.That(pool.BuildCount).IsEqualTo(3);
        await reused.ReleaseAsync(keepForReuse: false);
        await rebuilt.ReleaseAsync(keepForReuse: false);
        await Assert.That(pool.Count).IsEqualTo(0);
    }

    [TUnit.Core.Test]
    public async Task ReleaseAsync_WithoutReuseRetiresOnlyAfterTheLastLease()
    {
        var pool = new McpGatewaySharedIndexPool<McpGatewayRuntime.ToolGraphArtifact>(maximumIdleIndexes: 2);
        var first = await pool.AcquireAsync(FirstFingerprint, static () => CreateArtifactAsync("shared"), CancellationToken.None);
        var second = await pool.AcquireAsync(FirstFingerprint, static () => CreateArtifactAsync("unused"), CancellationToken.None);

        await first.ReleaseAsync(keepForReuse: false);
        await first.ReleaseAsync(keepForReuse: false);
        await Assert.That(second.Index.Graph.TripleCount).IsGreaterThan(0);
        await Assert.That(pool.Count).IsEqualTo(1);

        await second.ReleaseAsync(keepForReuse: false);

        await Assert.That(pool.Count).IsEqualTo(0);
        Assert.Throws<ObjectDisposedException>(() => _ = second.Index.Graph.TripleCount);
    }

    private static async Task<McpGatewayRuntime.ToolGraphArtifact> CreateArtifactAsync(string title)
    {
        var result = await new MarkdownKnowledgePipeline().BuildFromMarkdownAsync(
            $"# {title}\n\nA pooled graph artifact about {title} tool search.",
            $"{title}.md"
        );
        var graph = result.Graph;
        return new McpGatewayRuntime.ToolGraphArtifact(
            graph,
            graph.ToSnapshot(),
            [],
            new HashSet<string>(StringComparer.Ordinal),
            new Dictionary<string, KnowledgeGraphNode>(StringComparer.Ordinal),
            McpGatewayRuntime.GraphCandidateSearchIndex.Empty,
            McpGatewayRuntime.GraphNavigationIndex.Empty
        );
    }
}
