using System.Reflection;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;

namespace ManagedCode.MCPGateway.Tests;

public sealed partial class McpGatewaySearchTests
{
    [TUnit.Core.Test]
    public async Task BuildIndexAsync_ReportsEmbeddingCountMismatch()
    {
        var embeddingGenerator = new TestEmbeddingGenerator(
            new TestEmbeddingGeneratorOptions { ReturnMismatchedBatchCount = true }
        );

        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(
            ConfigureVectorSearchTools,
            embeddingGenerator
        );
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        var buildResult = await gateway.BuildIndexAsync();
        var searchResult = await gateway.SearchAsync("github pull requests", maxResults: 1);

        await Assert.That(buildResult.IsVectorSearchEnabled).IsFalse();
        await Assert.That(buildResult.VectorizedToolCount).IsEqualTo(0);
        await Assert
            .That(
                buildResult.Diagnostics.Any(static diagnostic =>
                    diagnostic.Code == "embedding_count_mismatch"
                )
            )
            .IsTrue();
        await Assert.That(buildResult.IsGraphSearchEnabled).IsTrue();
        await Assert.That(searchResult.RankingMode).IsEqualTo("graph");
    }

    [TUnit.Core.Test]
    public async Task McpGatewayOptions_DefaultSearchConfigurationUsesGraphAndTopFiveLimit()
    {
        var options = new McpGatewayOptions();

        await Assert.That(options.SearchStrategy).IsEqualTo(McpGatewaySearchStrategy.Graph);
        await Assert
            .That(options.MarkdownLdGraphSource)
            .IsEqualTo(McpGatewayMarkdownLdGraphSource.GeneratedToolGraph);
        await Assert.That(options.MarkdownLdGraphPath).IsNull();
        await Assert
            .That(options.SearchQueryNormalization)
            .IsEqualTo(McpGatewaySearchQueryNormalization.TranslateToEnglishWhenAvailable);
        await Assert.That(options.DefaultSearchLimit).IsEqualTo(McpGatewayOptions.DefaultSearchLimitValue);
        await Assert.That(options.MaxSearchResults).IsEqualTo(McpGatewayOptions.DefaultMaxSearchResults);
        await Assert.That(options.MaxDescriptorLength).IsEqualTo(McpGatewayOptions.DefaultMaxDescriptorLength);
    }

    [TUnit.Core.Test]
    public async Task SearchAsync_RejectsInvalidSearchLimitConfiguration()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.DefaultSearchLimit = 10;
            options.MaxSearchResults = 5;
        });

        Exception? exception = null;
        try
        {
            var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
            _ = await gateway.SearchAsync("github");
        }
        catch (Exception ex)
        {
            exception = ex;
        }

        await Assert.That(exception).IsTypeOf<ArgumentOutOfRangeException>();
        await Assert.That(exception!.Message).Contains(nameof(McpGatewayOptions.MaxSearchResults));
    }

    [TUnit.Core.Test]
    public async Task SearchAsync_RejectsInvalidDescriptorLengthConfiguration()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.MaxDescriptorLength = McpGatewayOptions.MinimumDescriptorLength - 1;
        });

        Exception? exception = null;
        try
        {
            var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
            _ = await gateway.SearchAsync("github");
        }
        catch (Exception ex)
        {
            exception = ex;
        }

        await Assert.That(exception).IsTypeOf<ArgumentOutOfRangeException>();
        await Assert.That(exception!.Message).Contains(nameof(McpGatewayOptions.MaxDescriptorLength));
    }

    [TUnit.Core.Test]
    public async Task McpGatewayOptions_UseMarkdownLdGraphFileSelectsFileSystemSource()
    {
        var options = new McpGatewayOptions();

        options.UseMarkdownLdGraphFile("/tmp/mcp-tools.graph.json");

        await Assert
            .That(options.MarkdownLdGraphSource)
            .IsEqualTo(McpGatewayMarkdownLdGraphSource.FileSystem);
        await Assert.That(options.MarkdownLdGraphPath).IsEqualTo("/tmp/mcp-tools.graph.json");
    }

    [TUnit.Core.Test]
    public async Task BuildIndexAsync_DefaultGraphStrategyDoesNotCallEmbeddingGenerator()
    {
        var embeddingGenerator = new TestEmbeddingGenerator(
            new TestEmbeddingGeneratorOptions { ThrowOnInput = static _ => true }
        );

        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(
            ConfigureSearchTools,
            embeddingGenerator
        );
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        var buildResult = await gateway.BuildIndexAsync();

        await Assert.That(buildResult.IsGraphSearchEnabled).IsTrue();
        await Assert.That(buildResult.IsVectorSearchEnabled).IsFalse();
        await Assert.That(buildResult.VectorizedToolCount).IsEqualTo(0);
        await Assert.That(embeddingGenerator.Calls.Count).IsEqualTo(0);
        await Assert
            .That(
                buildResult.Diagnostics.Any(static diagnostic =>
                    diagnostic.Code == "embedding_failed"
                )
            )
            .IsFalse();
    }

    [TUnit.Core.Test]
    public async Task McpGatewayClientFactory_UsesAssemblyBuildVersionForClientInfo()
    {
        var clientOptions = McpGatewayClientFactory.CreateClientOptions();
        var expectedVersion =
            typeof(McpGatewayClientFactory)
                .Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
            ?? typeof(McpGatewayClientFactory).Assembly.GetName().Version?.ToString();

        await Assert.That(clientOptions.ClientInfo?.Version).IsEqualTo(expectedVersion);
    }

    [TUnit.Core.Test]
    public async Task BuildIndexAsync_ReportsEmbeddingFailure()
    {
        var embeddingGenerator = new TestEmbeddingGenerator(
            new TestEmbeddingGeneratorOptions { ThrowOnInput = static _ => true }
        );

        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(
            ConfigureVectorSearchTools,
            embeddingGenerator
        );
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        var buildResult = await gateway.BuildIndexAsync();

        await Assert.That(buildResult.IsVectorSearchEnabled).IsFalse();
        await Assert.That(buildResult.VectorizedToolCount).IsEqualTo(0);
        await Assert
            .That(
                buildResult.Diagnostics.Any(static diagnostic =>
                    diagnostic.Code == "embedding_failed"
                )
            )
            .IsTrue();
    }

    [TUnit.Core.Test]
    public async Task BuildIndexAsync_SkipsDuplicateToolIds()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddTool(
                "local",
                TestFunctionFactory.CreateFunction(
                    SearchGitHub,
                    "github_search_issues",
                    "Search GitHub issues and pull requests by user query."
                )
            );
            options.AddTool(
                "local",
                TestFunctionFactory.CreateFunction(
                    SearchGitHubAgain,
                    "github_search_issues",
                    "Duplicate tool id for test coverage."
                )
            );
        });
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        var buildResult = await gateway.BuildIndexAsync();

        await Assert.That(buildResult.ToolCount).IsEqualTo(1);
        await Assert
            .That(
                buildResult.Diagnostics.Any(static diagnostic =>
                    diagnostic.Code == "duplicate_tool_id"
                )
            )
            .IsTrue();
    }

    [TUnit.Core.Test]
    public async Task BuildIndexAsync_RebuildsAfterNewToolIsRegistered()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddTool(
                "local",
                TestFunctionFactory.CreateFunction(
                    SearchGitHub,
                    "github_search_issues",
                    "Search GitHub issues and pull requests by user query."
                )
            );
        });

        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
        var registry = serviceProvider.GetRequiredService<IMcpGatewayRegistry>();

        var firstBuild = await gateway.BuildIndexAsync();

        registry.AddTool(
            "local",
            TestFunctionFactory.CreateFunction(
                SearchWeather,
                "weather_search_forecast",
                "Search weather forecast and temperature information by city name."
            )
        );

        var secondBuild = await gateway.BuildIndexAsync();

        await Assert.That(firstBuild.ToolCount).IsEqualTo(1);
        await Assert.That(secondBuild.ToolCount).IsEqualTo(2);
    }

    [TUnit.Core.Test]
    public async Task BuildIndexAsync_RebuildsAfterRegistryIsReplaced()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddTool(
                "local",
                TestFunctionFactory.CreateFunction(
                    SearchGitHub,
                    "github_search_issues",
                    "Search GitHub issues and pull requests by user query."
                )
            );
        });

        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
        var catalogRuntime = serviceProvider.GetRequiredService<IMcpGatewayCatalogRuntime>();

        var firstBuild = await gateway.BuildIndexAsync();

        var replacement = new McpGatewayOptions().AddTool(
            "runtime",
            TestFunctionFactory.CreateFunction(
                SearchWeather,
                "weather_search_forecast",
                "Search weather forecast and temperature information by city name."
            )
        );

        await catalogRuntime.ReconfigureAsync(replacement);
        var secondBuild = await gateway.BuildIndexAsync();
        var tools = await gateway.ListToolsAsync();

        await Assert.That(firstBuild.ToolCount).IsEqualTo(1);
        await Assert.That(secondBuild.ToolCount).IsEqualTo(1);
        await Assert
            .That(tools.Select(static tool => tool.ToolName).ToArray())
            .IsEquivalentTo(["weather_search_forecast"]);
    }

    [TUnit.Core.Test]
    public async Task McpGatewayFactory_CreatesCustomGatewayFromRegisteredFactoryService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMcpGateway();

        await using var serviceProvider = services.BuildServiceProvider();
        var factory = serviceProvider.GetRequiredService<IMcpGatewayFactory>();

        await using var gatewayHost = factory.Create(options =>
        {
            options.AddTool(
                "local",
                TestFunctionFactory.CreateFunction(
                    SearchGitHub,
                    "github_search_repositories",
                    "Search GitHub repositories by user query."
                )
            );
        });

        var buildResult = await gatewayHost.Gateway.BuildIndexAsync();
        var searchResult = await gatewayHost.Gateway.SearchAsync("find github repositories", 1);

        await Assert.That(buildResult.ToolCount).IsEqualTo(1);
        await Assert.That(searchResult.Matches.Count).IsEqualTo(1);
        await Assert.That(searchResult.Matches[0].ToolName).IsEqualTo("github_search_repositories");
        await Assert.That(gatewayHost.CatalogRuntime).IsNotNull();
    }

    [TUnit.Core.Test]
    public async Task AddMcpGateway_ResolvesCatalogRuntimeAsAdvancedService()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(static _ => { });

        var registry = serviceProvider.GetRequiredService<IMcpGatewayRegistry>();
        var catalogRuntime = serviceProvider.GetRequiredService<IMcpGatewayCatalogRuntime>();

        await Assert.That(ReferenceEquals(registry, catalogRuntime)).IsTrue();
    }

    [TUnit.Core.Test]
    public async Task Registry_ConcurrentToolRegistrationRetainsAllTools()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(static _ => { });
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
        var registry = serviceProvider.GetRequiredService<IMcpGatewayRegistry>();

        await Task.WhenAll(
            Enumerable
                .Range(0, 40)
                .Select(index =>
                    Task.Run(() =>
                        registry.AddTool(
                            "local",
                            TestFunctionFactory.CreateFunction(
                                SearchWeather,
                                $"weather_search_forecast_{index}",
                                $"Search weather forecast and temperature information for city {index}."
                            )
                        )
                    )
                )
        );

        var buildResult = await gateway.BuildIndexAsync();

        await Assert.That(buildResult.ToolCount).IsEqualTo(40);
    }

    [TUnit.Core.Test]
    public async Task AddMcpGateway_ResolvesRegistryAsSeparateService()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddTool(
                "local",
                TestFunctionFactory.CreateFunction(
                    SearchGitHub,
                    "github_search_issues",
                    "Search GitHub issues and pull requests by user query."
                )
            );
        });

        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
        var registry = serviceProvider.GetRequiredService<IMcpGatewayRegistry>();

        await Assert.That(ReferenceEquals(gateway, registry)).IsFalse();
    }

    [TUnit.Core.Test]
    public async Task AddMcpGateway_RegistryAlsoActsAsCatalogSource()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(static _ => { });
        var registry = serviceProvider.GetRequiredService<IMcpGatewayRegistry>();

        await Assert.That(registry).IsTypeOf<McpGatewayRegistry>();
        await Assert.That(registry is IMcpGatewayCatalogSource).IsTrue();
    }

    [TUnit.Core.Test]
    public async Task McpGateway_ThrowsClearErrorWhenRegistryServiceIsMissing()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions<McpGatewayOptions>();

        await using var serviceProvider = services.BuildServiceProvider();
        var options = serviceProvider.GetRequiredService<IOptions<McpGatewayOptions>>();
        var logger = serviceProvider.GetRequiredService<ILogger<McpGateway>>();
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();

        InvalidOperationException? exception = null;
        try
        {
            _ = new McpGateway(serviceProvider, options, logger, loggerFactory);
        }
        catch (InvalidOperationException ex)
        {
            exception = ex;
        }

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.Message).Contains("AddMcpGateway");
    }

    [TUnit.Core.Test]
    public async Task McpGateway_DoesNotExposeRegistryMutations()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(static _ => { });
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
        var registry = serviceProvider.GetRequiredService<IMcpGatewayRegistry>();

        registry.AddTool(
            "local",
            TestFunctionFactory.CreateFunction(
                SearchWeather,
                "weather_search_forecast",
                "Search weather forecast and temperature information by city name."
            )
        );

        var tools = await gateway.ListToolsAsync();

        await Assert
            .That(typeof(IMcpGatewayRegistry).IsAssignableFrom(gateway.GetType()))
            .IsFalse();
        await Assert.That(tools.Count).IsEqualTo(1);
        await Assert.That(tools.Single().ToolId).IsEqualTo("local:weather_search_forecast");
    }

    [TUnit.Core.Test]
    public async Task Registry_RejectsMutationsAfterServiceProviderIsDisposed()
    {
        var serviceProvider = GatewayTestServiceProviderFactory.Create(static _ => { });
        var registry = serviceProvider.GetRequiredService<IMcpGatewayRegistry>();

        await serviceProvider.DisposeAsync();

        ObjectDisposedException? exception = null;
        try
        {
            registry.AddTool(
                "local",
                TestFunctionFactory.CreateFunction(
                    SearchWeather,
                    "weather_search_forecast",
                    "Search weather forecast and temperature information by city name."
                )
            );
        }
        catch (ObjectDisposedException ex)
        {
            exception = ex;
        }

        await Assert.That(exception).IsNotNull();
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
                disposeClient: false
            );
        });
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        var firstBuild = await gateway.BuildIndexAsync();
        var secondBuild = await gateway.BuildIndexAsync();

        await Assert.That(attempts).IsEqualTo(2);
        await Assert.That(firstBuild.ToolCount).IsEqualTo(0);
        await Assert
            .That(
                firstBuild.Diagnostics.Any(static diagnostic =>
                    diagnostic.Code == "source_load_failed"
                )
            )
            .IsTrue();
        await Assert.That(secondBuild.ToolCount).IsEqualTo(3);
        await Assert
            .That(
                secondBuild.Diagnostics.Any(static diagnostic =>
                    diagnostic.Code == "source_load_failed"
                )
            )
            .IsFalse();
    }

    [TUnit.Core.Test]
    public async Task BuildIndexAsync_ConcurrentCallsShareSingleBuild()
    {
        await using var serverHost = await TestMcpServerHost.StartAsync();

        var attempts = 0;
        var factoryStarted = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseFactory = new TaskCompletionSource<McpClient>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
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
                disposeClient: false
            );
        });
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        var buildTasks = Enumerable.Range(0, 20).Select(_ => gateway.BuildIndexAsync()).ToArray();

        await factoryStarted.Task;
        releaseFactory.TrySetResult(serverHost.Client);

        var results = await Task.WhenAll(buildTasks);

        await Assert.That(attempts).IsEqualTo(1);
        await Assert.That(results.All(static result => result.ToolCount == 3)).IsTrue();
    }

    [TUnit.Core.Test]
    public async Task BuildIndexAsync_CancelsUnderlyingSourceLoadAndAllowsRetry()
    {
        await using var serverHost = await TestMcpServerHost.StartAsync();

        var attempts = 0;
        var factoryStarted = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddMcpClientFactory(
                "test-mcp",
                async cancellationToken =>
                {
                    var attempt = Interlocked.Increment(ref attempts);
                    if (attempt == 1)
                    {
                        factoryStarted.TrySetResult(null);
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }

                    return serverHost.Client;
                },
                disposeClient: false
            );
        });
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
        using var cancellationSource = new CancellationTokenSource();

        var firstBuildTask = gateway.BuildIndexAsync(cancellationSource.Token);
        await factoryStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        cancellationSource.Cancel();

        OperationCanceledException? cancellationException = null;
        try
        {
            await firstBuildTask.WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (OperationCanceledException ex)
        {
            cancellationException = ex;
        }

        var secondBuild = await gateway.BuildIndexAsync().WaitAsync(TimeSpan.FromSeconds(15));

        await Assert.That(cancellationException).IsNotNull();
        await Assert.That(attempts).IsEqualTo(2);
        await Assert.That(secondBuild.ToolCount).IsEqualTo(3);
    }

    [TUnit.Core.Test]
    public async Task DisposeAsync_CancelsRunningIndexBuild()
    {
        var registration = new BlockingToolSourceRegistration();
        var services = new ServiceCollection();
        services.AddLogging(static logging => logging.SetMinimumLevel(LogLevel.Debug));
        services.AddSingleton<IMcpGatewayCatalogSource>(new StaticCatalogSource(registration));
        services.AddMcpGateway();

        await using var serviceProvider = services.BuildServiceProvider();
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        var buildTask = gateway.BuildIndexAsync();
        await registration.LoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await gateway.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        await registration.LoadCancelled.Task.WaitAsync(TimeSpan.FromSeconds(15));

        OperationCanceledException? cancellationException = null;
        try
        {
            await buildTask.WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (OperationCanceledException ex)
        {
            cancellationException = ex;
        }

        await Assert.That(cancellationException).IsNotNull();
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
        var descriptor = tools.Single(static tool =>
            tool.ToolId == "test-mcp:github_repository_search"
        );

        await Assert.That(string.IsNullOrWhiteSpace(descriptor.InputSchemaJson)).IsFalse();
        await Assert
            .That(
                descriptor.RequiredArguments.Any(static argument =>
                    string.Equals(argument, "query", StringComparison.OrdinalIgnoreCase)
                )
            )
            .IsTrue();
    }

    [TUnit.Core.Test]
    public async Task ListToolsAsync_BuildsIndexOnDemand()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(
            ConfigureSearchTools
        );
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        var tools = await gateway.ListToolsAsync();

        await Assert.That(tools.Count).IsEqualTo(2);
        await Assert
            .That(tools.Any(static tool => tool.ToolId == "local:github_search_issues"))
            .IsTrue();
        await Assert
            .That(tools.Any(static tool => tool.ToolId == "local:weather_search_forecast"))
            .IsTrue();
    }

    private sealed class StaticCatalogSource(McpGatewayToolSourceRegistration registration)
        : IMcpGatewayCatalogSource
    {
        public McpGatewayCatalogSourceSnapshot CreateSnapshot() => new(0, [registration]);
    }

    private sealed class BlockingToolSourceRegistration()
        : McpGatewayToolSourceRegistration("blocking", displayName: null)
    {
        public TaskCompletionSource<object?> LoadCancelled { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public TaskCompletionSource<object?> LoadStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public override McpGatewaySourceRegistrationKind Kind => McpGatewaySourceRegistrationKind.Local;

        public override async ValueTask<IReadOnlyList<McpGatewayLoadedTool>> LoadToolsAsync(
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken
        )
        {
            _ = loggerFactory;
            LoadStarted.TrySetResult(null);

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                LoadCancelled.TrySetResult(null);
                throw;
            }

            return [];
        }
    }
}
