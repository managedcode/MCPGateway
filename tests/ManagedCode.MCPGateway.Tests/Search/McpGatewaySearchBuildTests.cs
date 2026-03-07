using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using System.Reflection;

namespace ManagedCode.MCPGateway.Tests;

public sealed partial class McpGatewaySearchTests
{
    [TUnit.Core.Test]
    public async Task BuildIndexAsync_ReportsEmbeddingCountMismatch()
    {
        var embeddingGenerator = new TestEmbeddingGenerator(new TestEmbeddingGeneratorOptions
        {
            ReturnMismatchedBatchCount = true
        });

        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(
            ConfigureSearchTools,
            embeddingGenerator);
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        var buildResult = await gateway.BuildIndexAsync();
        var searchResult = await gateway.SearchAsync("github pull requests", maxResults: 1);

        await Assert.That(buildResult.IsVectorSearchEnabled).IsFalse();
        await Assert.That(buildResult.VectorizedToolCount).IsEqualTo(0);
        await Assert.That(buildResult.Diagnostics.Any(static diagnostic => diagnostic.Code == "embedding_count_mismatch")).IsTrue();
        await Assert.That(searchResult.RankingMode).IsEqualTo("lexical");
    }

    [TUnit.Core.Test]
    public async Task McpGatewayOptions_DefaultSearchConfigurationUsesAutoWithChatGptTokenizerAndTopFiveLimit()
    {
        var options = new McpGatewayOptions();

        await Assert.That(options.SearchStrategy).IsEqualTo(McpGatewaySearchStrategy.Auto);
        await Assert.That(options.TokenSearchTokenizer).IsEqualTo(McpGatewayTokenSearchTokenizer.ChatGptO200kBase);
        await Assert.That(options.DefaultSearchLimit).IsEqualTo(5);
    }

    [TUnit.Core.Test]
    public async Task McpGatewayClientFactory_UsesAssemblyBuildVersionForClientInfo()
    {
        var clientOptions = McpGatewayClientFactory.CreateClientOptions();
        var expectedVersion = typeof(McpGatewayClientFactory).Assembly
                                  .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                              ?? typeof(McpGatewayClientFactory).Assembly.GetName().Version?.ToString();

        await Assert.That(clientOptions.ClientInfo?.Version).IsEqualTo(expectedVersion);
    }

    [TUnit.Core.Test]
    public async Task BuildIndexAsync_ReportsEmbeddingFailure()
    {
        var embeddingGenerator = new TestEmbeddingGenerator(new TestEmbeddingGeneratorOptions
        {
            ThrowOnInput = static _ => true
        });

        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(
            ConfigureSearchTools,
            embeddingGenerator);
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        var buildResult = await gateway.BuildIndexAsync();

        await Assert.That(buildResult.IsVectorSearchEnabled).IsFalse();
        await Assert.That(buildResult.VectorizedToolCount).IsEqualTo(0);
        await Assert.That(buildResult.Diagnostics.Any(static diagnostic => diagnostic.Code == "embedding_failed")).IsTrue();
    }

    [TUnit.Core.Test]
    public async Task BuildIndexAsync_SkipsDuplicateToolIds()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddTool("local", TestFunctionFactory.CreateFunction(SearchGitHub, "github_search_issues", "Search GitHub issues and pull requests by user query."));
            options.AddTool("local", TestFunctionFactory.CreateFunction(SearchGitHubAgain, "github_search_issues", "Duplicate tool id for test coverage."));
        });
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        var buildResult = await gateway.BuildIndexAsync();

        await Assert.That(buildResult.ToolCount).IsEqualTo(1);
        await Assert.That(buildResult.Diagnostics.Any(static diagnostic => diagnostic.Code == "duplicate_tool_id")).IsTrue();
    }

    [TUnit.Core.Test]
    public async Task BuildIndexAsync_RebuildsAfterNewToolIsRegistered()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddTool("local", TestFunctionFactory.CreateFunction(SearchGitHub, "github_search_issues", "Search GitHub issues and pull requests by user query."));
        });

        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
        var registry = serviceProvider.GetRequiredService<IMcpGatewayRegistry>();

        var firstBuild = await gateway.BuildIndexAsync();

        registry.AddTool(
            "local",
            TestFunctionFactory.CreateFunction(SearchWeather, "weather_search_forecast", "Search weather forecast and temperature information by city name."));

        var secondBuild = await gateway.BuildIndexAsync();

        await Assert.That(firstBuild.ToolCount).IsEqualTo(1);
        await Assert.That(secondBuild.ToolCount).IsEqualTo(2);
    }

    [TUnit.Core.Test]
    public async Task Registry_ConcurrentToolRegistrationRetainsAllTools()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(static _ => { });
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
        var registry = serviceProvider.GetRequiredService<IMcpGatewayRegistry>();

        await Task.WhenAll(Enumerable.Range(0, 40).Select(index => Task.Run(() =>
            registry.AddTool(
                "local",
                TestFunctionFactory.CreateFunction(
                    SearchWeather,
                    $"weather_search_forecast_{index}",
                    $"Search weather forecast and temperature information for city {index}.")))));

        var buildResult = await gateway.BuildIndexAsync();

        await Assert.That(buildResult.ToolCount).IsEqualTo(40);
    }

    [TUnit.Core.Test]
    public async Task AddManagedCodeMcpGateway_ResolvesRegistryAsSeparateService()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddTool("local", TestFunctionFactory.CreateFunction(SearchGitHub, "github_search_issues", "Search GitHub issues and pull requests by user query."));
        });

        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
        var registry = serviceProvider.GetRequiredService<IMcpGatewayRegistry>();

        await Assert.That(ReferenceEquals(gateway, registry)).IsFalse();
    }

    [TUnit.Core.Test]
    public async Task McpGateway_DoesNotExposeRegistryMutations()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(static _ => { });
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
        var registry = serviceProvider.GetRequiredService<IMcpGatewayRegistry>();

        registry.AddTool(
            "local",
            TestFunctionFactory.CreateFunction(SearchWeather, "weather_search_forecast", "Search weather forecast and temperature information by city name."));

        var tools = await gateway.ListToolsAsync();

        await Assert.That(typeof(IMcpGatewayRegistry).IsAssignableFrom(gateway.GetType())).IsFalse();
        await Assert.That(tools.Count).IsEqualTo(1);
        await Assert.That(tools.Single().ToolId).IsEqualTo("local:weather_search_forecast");
    }

    [TUnit.Core.Test]
    public async Task BuildIndexAsync_RetriesFailedMcpClientFactoryOnNextBuild()
    {
        await using var serverHost = await TestMcpServerHost.StartAsync();

        var attempts = 0;
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddMcpClientFactory(
                "test-mcp",
                _ =>
                {
                    attempts++;
                    if (attempts == 1)
                    {
                        throw new InvalidOperationException("temporary startup failure");
                    }

                    return ValueTask.FromResult(serverHost.Client);
                },
                disposeClient: false);
        });
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        var firstBuild = await gateway.BuildIndexAsync();
        var secondBuild = await gateway.BuildIndexAsync();

        await Assert.That(attempts).IsEqualTo(2);
        await Assert.That(firstBuild.ToolCount).IsEqualTo(0);
        await Assert.That(firstBuild.Diagnostics.Any(static diagnostic => diagnostic.Code == "source_load_failed")).IsTrue();
        await Assert.That(secondBuild.ToolCount).IsEqualTo(3);
        await Assert.That(secondBuild.Diagnostics.Any(static diagnostic => diagnostic.Code == "source_load_failed")).IsFalse();
    }

    [TUnit.Core.Test]
    public async Task BuildIndexAsync_ConcurrentCallsShareSingleBuild()
    {
        await using var serverHost = await TestMcpServerHost.StartAsync();

        var attempts = 0;
        var factoryStarted = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFactory = new TaskCompletionSource<McpClient>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddMcpClientFactory(
                "test-mcp",
                async _ =>
                {
                    Interlocked.Increment(ref attempts);
                    factoryStarted.TrySetResult(null);
                    return await releaseFactory.Task;
                },
                disposeClient: false);
        });
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        var firstBuildTask = gateway.BuildIndexAsync();
        var secondBuildTask = gateway.BuildIndexAsync();

        await factoryStarted.Task;
        releaseFactory.TrySetResult(serverHost.Client);

        var results = await Task.WhenAll(firstBuildTask, secondBuildTask);

        await Assert.That(attempts).IsEqualTo(1);
        await Assert.That(results[0].ToolCount).IsEqualTo(3);
        await Assert.That(results[1].ToolCount).IsEqualTo(3);
    }

    [TUnit.Core.Test]
    public async Task ListToolsAsync_ExtractsRequiredArgumentsFromSerializedMcpSchema()
    {
        await using var serverHost = await TestMcpServerHost.StartAsync();
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddMcpClient("test-mcp", serverHost.Client, disposeClient: false);
        });
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        var tools = await gateway.ListToolsAsync();
        var descriptor = tools.Single(static tool => tool.ToolId == "test-mcp:github_repository_search");

        await Assert.That(string.IsNullOrWhiteSpace(descriptor.InputSchemaJson)).IsFalse();
        await Assert.That(descriptor.RequiredArguments.Any(static argument => string.Equals(argument, "query", StringComparison.OrdinalIgnoreCase))).IsTrue();
    }

    [TUnit.Core.Test]
    public async Task ListToolsAsync_BuildsIndexOnDemand()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(ConfigureSearchTools);
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        var tools = await gateway.ListToolsAsync();

        await Assert.That(tools.Count).IsEqualTo(2);
        await Assert.That(tools.Any(static tool => tool.ToolId == "local:github_search_issues")).IsTrue();
        await Assert.That(tools.Any(static tool => tool.ToolId == "local:weather_search_forecast")).IsTrue();
    }
}
