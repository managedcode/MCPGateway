using ManagedCode.MCPGateway.Abstractions;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayChatClientIntegrationTests
{
    [TUnit.Core.Test]
    public async Task ChatOptions_AddMcpGatewayTools_ResolvesToolSetAndAvoidsDuplicates()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(static _ => { });

        var options = new ChatOptions()
            .AddMcpGatewayTools(serviceProvider)
            .AddMcpGatewayTools(serviceProvider);

        await Assert.That(options.Tools).IsNotNull();
        await Assert.That(options.Tools!.Count).IsEqualTo(2);
        await Assert.That(options.Tools.Select(static tool => tool.Name)).IsEquivalentTo(
            [
                McpGatewayToolSet.DefaultSearchToolName,
                McpGatewayToolSet.DefaultInvokeToolName
            ]);
    }

    [TUnit.Core.Test]
    public async Task ToolSet_AddTools_ReturnsNewListWithoutMutatingInput()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(static _ => { });
        var toolSet = serviceProvider.GetRequiredService<McpGatewayToolSet>();
        var existingTools = new List<AITool>
        {
            TestFunctionFactory.CreateFunction(
                static () => "existing",
                "existing_tool",
                "Existing tool.")
        };

        var composedTools = toolSet.AddTools(existingTools);

        await Assert.That(existingTools.Count).IsEqualTo(1);
        await Assert.That(composedTools.Count).IsEqualTo(3);
        await Assert.That(ReferenceEquals(existingTools, composedTools)).IsFalse();
    }

    [TUnit.Core.Test]
    public async Task AutoDiscoveryChatClient_IgnoresPrimitiveSearchResultPayloads()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(static _ => { });
        var modelClient = new TestChatClient(new TestChatClientOptions
        {
            Scenarios =
            [
                new TestChatClientScenario(
                    "return text",
                    static _ => true,
                    static _ => TestChatClientScenario.Text("ok"))
            ]
        });

        using var chatClient = modelClient.UseMcpGatewayAutoDiscovery(serviceProvider);

        var response = await chatClient.GetResponseAsync(
        [
            new ChatMessage(ChatRole.User, "Find the right tools."),
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("search-1", McpGatewayToolSet.DefaultSearchToolName, new Dictionary<string, object?>())]),
            new ChatMessage(ChatRole.Assistant, [new FunctionResultContent("search-1", JsonSerializer.SerializeToElement("not-an-object"))])
        ]);

        await Assert.That(response.Text).IsEqualTo("ok");
        await Assert.That(modelClient.Invocations.Count).IsEqualTo(1);
        await Assert.That(modelClient.Invocations[0].ToolNames).IsEquivalentTo(
            [
                McpGatewayToolSet.DefaultSearchToolName,
                McpGatewayToolSet.DefaultInvokeToolName
            ]);
    }

    [TUnit.Core.Test]
    public async Task AutoDiscoveryChatClient_ReplacesDiscoveredToolsWithoutEmbeddings()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(
            GatewayIntegrationTestSupport.ConfigureFiftyToolCatalog);
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
        await gateway.BuildIndexAsync();

        var modelClient = new TestChatClient(new TestChatClientOptions
        {
            Scenarios = GatewayIntegrationTestSupport.CreateAutoDiscoveryScenarios(useSemanticQueries: false)
        });

        using var chatClient = modelClient.UseMcpGatewayAutoDiscovery(
            serviceProvider,
            options => options.MaxDiscoveredTools = 2);

        var response = await chatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Find the right tools as you go and execute them.")],
            new ChatOptions
            {
                AllowMultipleToolCalls = false
            });

        var registeredTools = await gateway.ListToolsAsync();

        await Assert.That(registeredTools.Count).IsEqualTo(GatewayIntegrationTestSupport.CatalogToolCount);
        await Assert.That(response.Text).IsEqualTo(GatewayIntegrationTestSupport.FinalAssistantResponse);
        await GatewayIntegrationTestSupport.AssertAutoDiscoveryFlow(modelClient, "lexical");
    }

    [TUnit.Core.Test]
    public async Task AutoDiscoveryChatClient_ReplacesDiscoveredToolsWithEmbeddings()
    {
        var embeddingGenerator = GatewayIntegrationTestSupport.CreateAutoDiscoveryEmbeddingGenerator();

        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(
            GatewayIntegrationTestSupport.ConfigureFiftyToolCatalog,
            embeddingGenerator: embeddingGenerator);
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
        await gateway.BuildIndexAsync();

        var modelClient = new TestChatClient(new TestChatClientOptions
        {
            Scenarios = GatewayIntegrationTestSupport.CreateAutoDiscoveryScenarios(useSemanticQueries: true)
        });

        using var chatClient = modelClient.UseMcpGatewayAutoDiscovery(
            serviceProvider,
            options => options.MaxDiscoveredTools = 2);

        var response = await chatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Find the right tools as you go and execute them.")],
            new ChatOptions
            {
                AllowMultipleToolCalls = false
            });

        var registeredTools = await gateway.ListToolsAsync();

        await Assert.That(registeredTools.Count).IsEqualTo(GatewayIntegrationTestSupport.CatalogToolCount);
        await Assert.That(response.Text).IsEqualTo(GatewayIntegrationTestSupport.FinalAssistantResponse);
        await Assert.That(embeddingGenerator.Calls.Count >= 3).IsTrue();
        await GatewayIntegrationTestSupport.AssertAutoDiscoveryFlow(modelClient, "vector");
    }
}
