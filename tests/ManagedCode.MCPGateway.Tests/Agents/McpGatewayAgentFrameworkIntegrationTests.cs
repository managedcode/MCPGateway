using ManagedCode.MCPGateway.Abstractions;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayAgentFrameworkIntegrationTests
{
    [TUnit.Core.Test]
    public async Task ChatClientAgent_UsesAutoDiscoveryWithoutEmbeddings()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(
            GatewayIntegrationTestSupport.ConfigureFiftyToolCatalog);
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        await gateway.BuildIndexAsync();

        var modelClient = new TestChatClient(new TestChatClientOptions
        {
            Scenarios = GatewayIntegrationTestSupport.CreateAutoDiscoveryScenarios(useSemanticQueries: false)
        });
        using var autoDiscoveryClient = modelClient.UseMcpGatewayAutoDiscovery(
            serviceProvider,
            options => options.MaxDiscoveredTools = 2);

        var agent = new ChatClientAgent(
            autoDiscoveryClient,
            instructions: "Use the gateway tools when the user asks for catalog actions.",
            name: "gateway-agent",
            description: "Agent integration test",
            tools: [],
            loggerFactory: loggerFactory,
            services: serviceProvider);

        var response = await agent.RunAsync(
            "Find the right tools as you go and execute them.",
            session: null,
            options: new ChatClientAgentRunOptions(new ChatOptions
            {
                AllowMultipleToolCalls = false
            }),
            cancellationToken: default);

        var registeredTools = await gateway.ListToolsAsync();

        await Assert.That(registeredTools.Count).IsEqualTo(GatewayIntegrationTestSupport.CatalogToolCount);
        await Assert.That(response.Text).IsEqualTo(GatewayIntegrationTestSupport.FinalAssistantResponse);
        await GatewayIntegrationTestSupport.AssertAutoDiscoveryFlow(modelClient, "lexical");
    }

    [TUnit.Core.Test]
    public async Task ChatClientAgent_UsesAutoDiscoveryWithEmbeddings()
    {
        var embeddingGenerator = GatewayIntegrationTestSupport.CreateAutoDiscoveryEmbeddingGenerator();

        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(
            GatewayIntegrationTestSupport.ConfigureFiftyToolCatalog,
            embeddingGenerator: embeddingGenerator);
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        await gateway.BuildIndexAsync();

        var modelClient = new TestChatClient(new TestChatClientOptions
        {
            Scenarios = GatewayIntegrationTestSupport.CreateAutoDiscoveryScenarios(useSemanticQueries: true)
        });
        using var autoDiscoveryClient = modelClient.UseMcpGatewayAutoDiscovery(
            serviceProvider,
            options => options.MaxDiscoveredTools = 2);

        var agent = new ChatClientAgent(
            autoDiscoveryClient,
            instructions: "Use the gateway tools when the user asks for catalog actions.",
            name: "gateway-agent",
            description: "Agent integration test",
            tools: [],
            loggerFactory: loggerFactory,
            services: serviceProvider);

        var response = await agent.RunAsync(
            "Find the right tools as you go and execute them.",
            session: null,
            options: new ChatClientAgentRunOptions(new ChatOptions
            {
                AllowMultipleToolCalls = false
            }),
            cancellationToken: default);

        var registeredTools = await gateway.ListToolsAsync();

        await Assert.That(registeredTools.Count).IsEqualTo(GatewayIntegrationTestSupport.CatalogToolCount);
        await Assert.That(response.Text).IsEqualTo(GatewayIntegrationTestSupport.FinalAssistantResponse);
        await Assert.That(embeddingGenerator.Calls.Count >= 3).IsTrue();
        await GatewayIntegrationTestSupport.AssertAutoDiscoveryFlow(modelClient, "vector");
    }
}
