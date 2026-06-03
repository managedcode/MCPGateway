using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayAutoDiscoveryChatClientAdditionalTests
{
    [TUnit.Core.Test]
    public async Task GetStreamingResponseAsync_UsesGatewayToolsAndExposesSelfService()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(static _ => { });
        var toolSet = serviceProvider.GetRequiredService<McpGatewayToolSet>();
        var modelClient = new TestChatClient(
            new TestChatClientOptions
            {
                Scenarios =
                [
                    new TestChatClientScenario(
                        "return text",
                        static _ => true,
                        static _ => TestChatClientScenario.Text("stream ok")
                    ),
                ],
            }
        );

        using var chatClient = new McpGatewayAutoDiscoveryChatClient(modelClient, toolSet);

        var resolvedService = chatClient.GetService(typeof(McpGatewayAutoDiscoveryChatClient));
        var updates = new List<ChatResponseUpdate>();
        await foreach (
            var update in chatClient.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "Find the right tools.")]
            )
        )
        {
            updates.Add(update);
        }

        await Assert.That(ReferenceEquals(resolvedService, chatClient)).IsTrue();
        await Assert.That(updates.Count > 0).IsTrue();
        await Assert.That(modelClient.Invocations.Count).IsEqualTo(1);
        await Assert
            .That(modelClient.Invocations[0].ToolNames)
            .IsEquivalentTo([
                McpGatewayToolSet.DefaultSearchToolName,
                McpGatewayToolSet.DefaultRouteToolName,
                McpGatewayToolSet.DefaultInvokeToolName,
            ]);
    }

    [TUnit.Core.Test]
    public async Task GetResponseAsync_IgnoresInvalidJsonObjectSearchResultPayloads()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(static _ => { });
        var modelClient = new TestChatClient(
            new TestChatClientOptions
            {
                Scenarios =
                [
                    new TestChatClientScenario(
                        "return text",
                        static _ => true,
                        static _ => TestChatClientScenario.Text("ok")
                    ),
                ],
            }
        );

        using var chatClient = modelClient.UseMcpGatewayAutoDiscovery(serviceProvider);

        var response = await chatClient.GetResponseAsync([
            new ChatMessage(ChatRole.User, "Find the right tools."),
            new ChatMessage(
                ChatRole.Assistant,
                [
                    new FunctionCallContent(
                        "search-1",
                        McpGatewayToolSet.DefaultSearchToolName,
                        new Dictionary<string, object?>()
                    ),
                ]
            ),
            new ChatMessage(
                ChatRole.Assistant,
                [
                    new FunctionResultContent(
                        "search-1",
                        JsonSerializer.SerializeToElement(
                            new
                            {
                                matches = new { invalid = true },
                                rankingMode = "graph",
                            }
                        )
                    ),
                ]
            ),
        ]);

        await Assert.That(response.Text).IsEqualTo("ok");
        await Assert.That(modelClient.Invocations.Count).IsEqualTo(1);
        await Assert
            .That(modelClient.Invocations[0].ToolNames)
            .IsEquivalentTo([
                McpGatewayToolSet.DefaultSearchToolName,
                McpGatewayToolSet.DefaultRouteToolName,
                McpGatewayToolSet.DefaultInvokeToolName,
            ]);
    }

    [TUnit.Core.Test]
    public async Task GetResponseAsync_AddsUniqueNextStepDiscoveryTools()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(static _ => { });
        var modelClient = new TestChatClient(
            new TestChatClientOptions
            {
                Scenarios =
                [
                    new TestChatClientScenario(
                        "return text",
                        static _ => true,
                        static _ => TestChatClientScenario.Text("ok")
                    ),
                ],
            }
        );
        using var chatClient = modelClient.UseMcpGatewayAutoDiscovery(
            serviceProvider,
            options => options.MaxDiscoveredTools = 5
        );
        var searchResult = new McpGatewaySearchResult(
            [CreateSearchMatch("story_item_search", "story_item_search")],
            [],
            "graph"
        )
        {
            RelatedMatches =
            [
                CreateSearchMatch("story_comments_list", "story_comments_list"),
            ],
            NextStepMatches =
            [
                CreateSearchMatch("people_profile_search", "people_profile_search"),
            ],
        };

        var response = await chatClient.GetResponseAsync([
            new ChatMessage(ChatRole.User, "Find the story tools."),
            new ChatMessage(
                ChatRole.Assistant,
                [
                    new FunctionCallContent(
                        "search-1",
                        McpGatewayToolSet.DefaultSearchToolName,
                        new Dictionary<string, object?>()
                    ),
                ]
            ),
            new ChatMessage(
                ChatRole.Assistant,
                [new FunctionResultContent("search-1", searchResult)]
            ),
        ]);

        await Assert.That(response.Text).IsEqualTo("ok");
        await Assert
            .That(modelClient.Invocations[0].ToolNames)
            .IsEquivalentTo([
                McpGatewayToolSet.DefaultSearchToolName,
                McpGatewayToolSet.DefaultRouteToolName,
                McpGatewayToolSet.DefaultInvokeToolName,
                "story_item_search",
                "story_comments_list",
                "people_profile_search",
            ]);
    }

    [TUnit.Core.Test]
    public async Task GetResponseAsync_HashShortensLongDiscoveredToolNamesFromSearchResults()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(static _ => { });
        var modelClient = new TestChatClient(
            new TestChatClientOptions
            {
                Scenarios =
                [
                    new TestChatClientScenario(
                        "return text",
                        static _ => true,
                        static _ => TestChatClientScenario.Text("ok")
                    ),
                ],
            }
        );
        using var chatClient = modelClient.UseMcpGatewayAutoDiscovery(
            serviceProvider,
            options => options.MaxDiscoveredTools = 1
        );
        var rawToolName = string.Concat(
            "Notion Fetch Page Details From Customer Workspace / Shared Database ",
            new string('A', 90)
        );
        var searchResult = new McpGatewaySearchResult(
            [CreateSearchMatch("gateway_tool_id", rawToolName)],
            [],
            "graph"
        );

        var response = await chatClient.GetResponseAsync([
            new ChatMessage(ChatRole.User, "Find the story tools."),
            new ChatMessage(
                ChatRole.Assistant,
                [
                    new FunctionCallContent(
                        "search-1",
                        McpGatewayToolSet.DefaultSearchToolName,
                        new Dictionary<string, object?>()
                    ),
                ]
            ),
            new ChatMessage(
                ChatRole.Assistant,
                [new FunctionResultContent("search-1", searchResult)]
            ),
        ]);
        var discoveredToolName = modelClient
            .Invocations[0]
            .ToolNames
            .Single(static name =>
                name is not McpGatewayToolSet.DefaultSearchToolName
                    and not McpGatewayToolSet.DefaultRouteToolName
                    and not McpGatewayToolSet.DefaultInvokeToolName
            );

        await Assert.That(response.Text).IsEqualTo("ok");
        await Assert.That(discoveredToolName).IsNotEqualTo("gateway_tool_id");
        await Assert.That(IsMcpSafeToolName(discoveredToolName)).IsTrue();
        await Assert.That(HasHashSuffix(discoveredToolName)).IsTrue();
    }

    [TUnit.Core.Test]
    public async Task GetResponseAsync_UsesNormalizedCustomSearchToolNameForDiscovery()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(static _ => { });
        var modelClient = new TestChatClient(
            new TestChatClientOptions
            {
                Scenarios =
                [
                    new TestChatClientScenario(
                        "return text",
                        static _ => true,
                        static _ => TestChatClientScenario.Text("ok")
                    ),
                ],
            }
        );
        using var chatClient = modelClient.UseMcpGatewayAutoDiscovery(
            serviceProvider,
            options =>
            {
                options.SearchToolName = "Gateway:Search";
                options.RouteToolName = "Gateway Route";
                options.InvokeToolName = "Gateway/Invoke";
            }
        );
        var searchResult = new McpGatewaySearchResult(
            [CreateSearchMatch("story_item_search", "story_item_search")],
            [],
            "graph"
        );

        var response = await chatClient.GetResponseAsync([
            new ChatMessage(ChatRole.User, "Find the story tools."),
            new ChatMessage(
                ChatRole.Assistant,
                [
                    new FunctionCallContent(
                        "search-1",
                        "gateway_search",
                        new Dictionary<string, object?>()
                    ),
                ]
            ),
            new ChatMessage(
                ChatRole.Assistant,
                [new FunctionResultContent("search-1", searchResult)]
            ),
        ]);

        await Assert.That(response.Text).IsEqualTo("ok");
        await Assert
            .That(modelClient.Invocations[0].ToolNames)
            .IsEquivalentTo(["gateway_search", "gateway_route", "gateway_invoke", "story_item_search"]);
    }

    private static McpGatewaySearchMatch CreateSearchMatch(string toolId, string toolName) =>
        new(
            toolId,
            "local",
            McpGatewaySourceKind.Local,
            toolName,
            null,
            "Test search match.",
            [],
            null,
            1d
        );

    private static bool IsMcpSafeToolName(string toolName) =>
        toolName.Length is > 0 and <= McpGatewayProtocolName.MaxNameLength
        && toolName.All(static character =>
            character is >= 'a' and <= 'z'
            || character is >= '0' and <= '9'
            || character is '_' or '-'
        );

    private static bool HasHashSuffix(string toolName)
    {
        if (toolName.Length < 9 || toolName[^9] != '_')
        {
            return false;
        }

        return toolName[^8..].All(static character =>
            character is >= 'a' and <= 'f' || character is >= '0' and <= '9'
        );
    }
}
