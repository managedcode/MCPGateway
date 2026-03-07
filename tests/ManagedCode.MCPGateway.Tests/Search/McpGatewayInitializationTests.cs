using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ManagedCode.MCPGateway.Tests;

public sealed partial class McpGatewaySearchTests
{
    [TUnit.Core.Test]
    public async Task InitializeManagedCodeMcpGatewayAsync_BuildsIndexThroughServiceProviderExtension()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(ConfigureSearchTools);

        var buildResult = await serviceProvider.InitializeManagedCodeMcpGatewayAsync();
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
        var tools = await gateway.ListToolsAsync();

        await Assert.That(buildResult.ToolCount).IsEqualTo(2);
        await Assert.That(tools.Count).IsEqualTo(2);
    }

    [TUnit.Core.Test]
    public async Task AddManagedCodeMcpGatewayIndexWarmup_StartsBackgroundIndexBuild()
    {
        var probeGateway = new WarmupProbeGateway();
        var services = new ServiceCollection();
        services.AddLogging(static logging => logging.SetMinimumLevel(LogLevel.Debug));
        services.AddSingleton<IMcpGateway>(probeGateway);
        services.AddManagedCodeMcpGatewayIndexWarmup();

        await using var serviceProvider = services.BuildServiceProvider();
        var hostedServices = serviceProvider.GetServices<IHostedService>().ToList();
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await Assert.That(hostedServices.Count).IsEqualTo(1);

        var hostedService = hostedServices.Single();
        await hostedService.StartAsync(cancellationSource.Token);
        await probeGateway.BuildStarted.WaitAsync(cancellationSource.Token);
        await hostedService.StopAsync(cancellationSource.Token);

        await Assert.That(probeGateway.BuildIndexCallCount).IsEqualTo(1);
    }
}

internal sealed class WarmupProbeGateway : IMcpGateway
{
    private readonly TaskCompletionSource<object?> _buildStarted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _buildIndexCallCount;

    public int BuildIndexCallCount => Volatile.Read(ref _buildIndexCallCount);

    public Task BuildStarted => _buildStarted.Task;

    public Task<McpGatewayIndexBuildResult> BuildIndexAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _buildIndexCallCount);
        _buildStarted.TrySetResult(null);
        return Task.FromResult(new McpGatewayIndexBuildResult(0, 0, false, []));
    }

    public Task<IReadOnlyList<McpGatewayToolDescriptor>> ListToolsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<McpGatewayToolDescriptor>>([]);

    public Task<McpGatewaySearchResult> SearchAsync(
        string? query,
        int? maxResults = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new McpGatewaySearchResult([], [], string.Empty));

    public Task<McpGatewaySearchResult> SearchAsync(
        McpGatewaySearchRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new McpGatewaySearchResult([], [], string.Empty));

    public Task<McpGatewayInvokeResult> InvokeAsync(
        McpGatewayInvokeRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new McpGatewayInvokeResult(false, string.Empty, string.Empty, string.Empty, null));

    public IReadOnlyList<Microsoft.Extensions.AI.AITool> CreateMetaTools(
        string searchToolName = McpGatewayToolSet.DefaultSearchToolName,
        string invokeToolName = McpGatewayToolSet.DefaultInvokeToolName)
        => [];

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
