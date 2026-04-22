using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ManagedCode.MCPGateway.Tests;

public sealed partial class McpGatewaySearchTests
{
    [TUnit.Core.Test]
    public async Task SearchAsync_DoesNotUseNormalizationClientWhenNormalizationIsDisabled()
    {
        var chatClient = new TestChatClient(
            new TestChatClientOptions { ThrowOnInput = static _ => true }
        );

        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(
            options =>
            {
                options.SearchQueryNormalization = McpGatewaySearchQueryNormalization.Disabled;
                ConfigureTravelMarkdownLdTools(options);
            },
            searchQueryChatClient: chatClient
        );
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        await gateway.BuildIndexAsync();
        var searchResult = await gateway.SearchAsync(
            "hotel with breakfast near city center",
            maxResults: 1
        );

        await Assert.That(searchResult.Matches[0].ToolId).IsEqualTo("local:travel_hotel_search");
        await Assert.That(chatClient.Calls.Count).IsEqualTo(0);
        await Assert
            .That(
                searchResult.Diagnostics.Any(static diagnostic =>
                    diagnostic.Code == "query_normalized"
                )
            )
            .IsFalse();
    }

    [TUnit.Core.Test]
    public async Task SearchAsync_CachesNullNormalizationWhenRewriteReturnsWhitespace()
    {
        var chatClient = new TestChatClient(
            new TestChatClientOptions { RewriteQuery = static _ => "   \n\t   " }
        );

        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(
            ConfigureTravelMarkdownLdTools,
            searchQueryChatClient: chatClient,
            useInMemorySearchCache: true
        );
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        await gateway.BuildIndexAsync();
        var firstSearch = await gateway.SearchAsync(
            "hotel with breakfast near city center",
            maxResults: 1
        );
        var secondSearch = await gateway.SearchAsync(
            "hotel with breakfast near city center",
            maxResults: 2
        );

        await Assert.That(firstSearch.Matches[0].ToolId).IsEqualTo("local:travel_hotel_search");
        await Assert.That(secondSearch.Matches[0].ToolId).IsEqualTo("local:travel_hotel_search");
        await Assert.That(secondSearch.Matches.Count).IsEqualTo(2);
        await Assert.That(chatClient.Calls.Count).IsEqualTo(1);
        await Assert
            .That(
                firstSearch.Diagnostics.Concat(secondSearch.Diagnostics).Any(static diagnostic =>
                    diagnostic.Code == "query_normalized"
                )
            )
            .IsFalse();
    }

    [TUnit.Core.Test]
    public async Task SearchAsync_CachesNullNormalizationWhenRewriteMatchesOriginalQuery()
    {
        var chatClient = new TestChatClient(
            new TestChatClientOptions
            {
                RewriteQuery = static _ => "  `HOTEL WITH BREAKFAST NEAR CITY CENTER`\n",
            }
        );

        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(
            ConfigureTravelMarkdownLdTools,
            searchQueryChatClient: chatClient,
            useInMemorySearchCache: true
        );
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        await gateway.BuildIndexAsync();
        var firstSearch = await gateway.SearchAsync(
            "hotel with breakfast near city center",
            maxResults: 1
        );
        var secondSearch = await gateway.SearchAsync(
            "hotel with breakfast near city center",
            maxResults: 3
        );

        await Assert.That(firstSearch.Matches[0].ToolId).IsEqualTo("local:travel_hotel_search");
        await Assert.That(secondSearch.Matches[0].ToolId).IsEqualTo("local:travel_hotel_search");
        await Assert.That(secondSearch.Matches.Count).IsEqualTo(3);
        await Assert.That(chatClient.Calls.Count).IsEqualTo(1);
        await Assert
            .That(
                firstSearch.Diagnostics.Concat(secondSearch.Diagnostics).Any(static diagnostic =>
                    diagnostic.Code == "query_normalized"
                )
            )
            .IsFalse();
    }

    [TUnit.Core.Test]
    public async Task SearchAsync_UsesRootProviderSearchQueryClientWhenScopesAreUnavailable()
    {
        var chatClient = new TestChatClient(
            new TestChatClientOptions
            {
                RewriteQuery = static query =>
                    query.Contains("petit déjeuner", StringComparison.Ordinal)
                        ? "hotel with breakfast near city center"
                        : query,
            }
        );

        var services = new ServiceCollection();
        services.AddLogging(static logging => logging.SetMinimumLevel(LogLevel.Debug));
        services.AddMcpGateway();
        services.AddKeyedSingleton<IChatClient>(
            McpGatewayServiceKeys.SearchQueryChatClient,
            chatClient
        );

        await using var rootProvider = services.BuildServiceProvider();
        var factory = new McpGatewayFactory(
            new NonScopedRootServiceProvider(rootProvider),
            rootProvider.GetRequiredService<ILoggerFactory>()
        );

        await using var gatewayInstance = factory.Create(ConfigureTravelMarkdownLdTools);

        await gatewayInstance.Gateway.BuildIndexAsync();
        var searchResult = await gatewayInstance.Gateway.SearchAsync(
            "trouver un hôtel avec petit déjeuner près du centre",
            maxResults: 1
        );

        await Assert.That(searchResult.Matches[0].ToolId).IsEqualTo("local:travel_hotel_search");
        await Assert.That(chatClient.Calls.Count).IsEqualTo(1);
        await Assert
            .That(
                searchResult.Diagnostics.Any(static diagnostic =>
                    diagnostic.Code == "query_normalized"
                )
            )
            .IsTrue();
    }

    private sealed class NonScopedRootServiceProvider(ServiceProvider rootProvider)
        : IServiceProvider, IKeyedServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IServiceScopeFactory)
                ? null
                : rootProvider.GetService(serviceType);

        public object? GetKeyedService(Type serviceType, object? serviceKey) =>
            rootProvider is IKeyedServiceProvider keyedServiceProvider
                ? keyedServiceProvider.GetKeyedService(serviceType, serviceKey)
                : null;

        public object GetRequiredKeyedService(Type serviceType, object? serviceKey) =>
            GetKeyedService(serviceType, serviceKey)
            ?? throw new InvalidOperationException(
                $"No keyed service '{serviceType}' is registered for key '{serviceKey}'."
            );
    }
}
