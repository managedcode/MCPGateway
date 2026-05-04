using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Tests;

public sealed partial class McpGatewaySearchTests
{
    [TUnit.Core.Test]
    public async Task SearchAsync_MarkdownLdGraphSuppressesLowConfidenceIrrelevantMultilingualResults()
    {
        var chatClient = CreateNotificationsRewriteClient();

        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(
            ConfigureFamilyRelationshipTools,
            searchQueryChatClient: chatClient
        );
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        await gateway.BuildIndexAsync();
        var searchResult = await gateway.SearchAsync("а що у мен з нотіфікешенми", maxResults: 3);

        await Assert.That(searchResult.RankingMode).IsEqualTo("graph");
        await Assert
            .That(
                searchResult.Diagnostics.Any(static diagnostic =>
                    diagnostic.Code == "query_normalized"
                )
            )
            .IsTrue();
        await Assert
            .That(
                searchResult.Diagnostics.Any(static diagnostic =>
                    diagnostic.Code == "low_confidence_results"
                )
            )
            .IsTrue();
        await Assert.That(searchResult.Matches.Count > 0).IsTrue();
        await Assert.That(searchResult.Matches.Count <= 3).IsTrue();
        await Assert.That(searchResult.Matches.All(static match => match.Score < 0.35d)).IsTrue();
    }

    [TUnit.Core.Test]
    public async Task SearchAsync_MarkdownLdGraphRanksNormalizedNotificationToolAheadOfFamilyRelationshipTools()
    {
        var chatClient = CreateNotificationsRewriteClient();

        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(
            ConfigureNotificationAndFamilyTools,
            searchQueryChatClient: chatClient
        );
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        await gateway.BuildIndexAsync();
        var searchResult = await gateway.SearchAsync("а що у мен з нотіфікешенми", maxResults: 3);

        await Assert.That(searchResult.RankingMode).IsEqualTo("graph");
        await Assert
            .That(
                searchResult.Diagnostics.Any(static diagnostic =>
                    diagnostic.Code == "low_confidence_results"
                )
            )
            .IsFalse();
        await Assert
            .That(searchResult.Matches[0].ToolId)
            .IsEqualTo("local:notification_activity_search");
        await Assert.That(searchResult.Matches[0].Score).IsGreaterThan(0.5d);
        await Assert
            .That(
                searchResult.Matches.All(static match =>
                    match.ToolId != "local:storied_person_add_father" || match.Score < 1d
                )
            )
            .IsTrue();
    }

    [TUnit.Core.Test]
    public async Task SearchAsync_MarkdownLdGraphKeepsTypoHeavyRelevantMatchesAboveConfidenceThreshold()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(
            ConfigureDefaultMarkdownLdSearchTools
        );
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        await gateway.BuildIndexAsync();
        var searchResult = await gateway.SearchAsync("track shipmnt 1z999", maxResults: 1);

        await Assert.That(searchResult.RankingMode).IsEqualTo("graph");
        await Assert
            .That(
                searchResult.Diagnostics.Any(static diagnostic =>
                    diagnostic.Code == "low_confidence_results"
                )
            )
            .IsFalse();
        await Assert
            .That(searchResult.Matches[0].ToolId)
            .IsEqualTo("local:commerce_shipping_tracking");
        await Assert.That(searchResult.Matches[0].Score).IsGreaterThan(0.5d);
    }

    private static TestChatClient CreateNotificationsRewriteClient() =>
        new(
            new TestChatClientOptions
            {
                RewriteQuery = static query =>
                    query.Contains("нотіфік", StringComparison.Ordinal)
                        ? "notification inbox alerts unread activity"
                        : query,
            }
        );

    private static void ConfigureFamilyRelationshipTools(McpGatewayOptions options)
    {
        options.AddTool(
            "local",
            TestFunctionFactory.CreateFunction(
                SearchWeather,
                "storied_person_add_father",
                "Add a father relationship for a person in a tree."
            )
        );
        options.AddTool(
            "local",
            TestFunctionFactory.CreateFunction(
                SearchWeather,
                "storied_person_add_mother",
                "Add a mother relationship for a person in a tree."
            )
        );
        options.AddTool(
            "local",
            TestFunctionFactory.CreateFunction(
                SearchWeather,
                "storied_person_add_potential_parent",
                "Apply a suggested potential parent link to a person in a tree."
            )
        );
    }

    private static void ConfigureNotificationAndFamilyTools(McpGatewayOptions options)
    {
        ConfigureFamilyRelationshipTools(options);
        options.AddTool(
            "local",
            TestFunctionFactory.CreateFunction(
                SearchWeather,
                "notification_activity_search",
                "List notification inbox alerts, unread activity, mentions, and message updates for the current user."
            )
        );
    }
}
