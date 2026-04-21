using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayPromptCatalogTests
{
    [TUnit.Core.Test]
    public async Task ListPromptsAsync_ReturnsPromptDescriptorsFromMcpSource()
    {
        await using var serverHost = await TestMcpServerHost.StartAsync();
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddMcpClient("test-mcp", serverHost.Client, disposeClient: false);
        });
        var promptCatalog = serviceProvider.GetRequiredService<IMcpGatewayPromptCatalog>();

        var prompts = await promptCatalog.ListPromptsAsync();
        var descriptor = prompts.Single(static prompt =>
            prompt.PromptId == "test-mcp:repository_triage_system_prompt"
        );

        await Assert.That(prompts.Count).IsEqualTo(2);
        await Assert.That(descriptor.SourceKind).IsEqualTo(McpGatewaySourceKind.CustomMcpClient);
        await Assert.That(descriptor.DisplayName).IsEqualTo("Repository triage");
        await Assert.That(descriptor.Description).Contains("triage system prompt");
        await Assert.That(descriptor.RequiredArguments).IsEquivalentTo(["repository"]);
        await Assert
            .That(descriptor.Arguments.Select(static argument => argument.Name).ToArray())
            .IsEquivalentTo(["repository", "locale"]);
    }

    [TUnit.Core.Test]
    public async Task GetPromptAsync_ReturnsRenderedPromptMessages()
    {
        await using var serverHost = await TestMcpServerHost.StartAsync();
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddMcpClient("test-mcp", serverHost.Client, disposeClient: false);
        });
        var promptCatalog = serviceProvider.GetRequiredService<IMcpGatewayPromptCatalog>();

        var prompt = await promptCatalog.GetPromptAsync(
            new McpGatewayPromptRequest(
                SourceId: "test-mcp",
                PromptName: "repository_triage_system_prompt",
                Arguments: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["repository"] = "ManagedCode.MCPGateway",
                    ["locale"] = "uk",
                }
            )
        );

        await Assert.That(prompt).IsNotNull();
        await Assert.That(prompt!.PromptId).IsEqualTo("test-mcp:repository_triage_system_prompt");
        await Assert.That(prompt.Messages.Count).IsEqualTo(1);
        await Assert.That(prompt.Messages[0].Role).IsEqualTo("User");
        await Assert.That(prompt.Messages[0].Text).Contains("ManagedCode.MCPGateway");
        await Assert.That(prompt.Messages[0].Text).Contains("uk");
        await Assert.That(prompt.Messages[0].Content).IsNotNull();
    }

    [TUnit.Core.Test]
    public async Task FactoryCreatedInstance_ExposesPromptCatalog()
    {
        await using var serverHost = await TestMcpServerHost.StartAsync();
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(_ => { });
        var factory = serviceProvider.GetRequiredService<IMcpGatewayFactory>();

        await using var gatewayHost = factory.Create(options =>
        {
            options.AddMcpClient("factory-mcp", serverHost.Client, disposeClient: false);
        });

        var prompts = await gatewayHost.PromptCatalog.ListPromptsAsync();

        await Assert
            .That(
                prompts.Any(static prompt =>
                    prompt.PromptId == "factory-mcp:repository_triage_system_prompt"
                )
            )
            .IsTrue();
    }
}
