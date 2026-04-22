using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayMcpServerIntegrationTests
{
    [TUnit.Core.Test]
    public async Task ListToolsAsync_ExposesAggregatedToolsFromMultipleUpstreamServers()
    {
        await using var primaryServer = await TestMcpServerHost.StartAsync();
        await using var graphServer = await TestMcpServerHost.StartGraphAsync();
        await using var operationsServer = await TestMcpServerHost.StartOperationsAsync();
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(options =>
        {
            options.AddMcpClient("source-a", primaryServer.Client, disposeClient: false);
            options.AddMcpClient("source-b", graphServer.Client, disposeClient: false);
            options.AddMcpClient("source-c", operationsServer.Client, disposeClient: false);
        });

        var tools = await gatewayServer.Client.ListToolsAsync();

        await Assert.That(gatewayServer.Client.ServerCapabilities.Tools).IsNotNull();
        await Assert.That(tools.Count).IsEqualTo(9);
        await Assert
            .That(tools.Any(static tool => tool.Name == "source-a:github_repository_search"))
            .IsTrue();
        await Assert
            .That(tools.Any(static tool => tool.Name == "source-b:story_item_detail"))
            .IsTrue();
        await Assert
            .That(tools.Any(static tool => tool.Name == "source-c:incident_status_lookup"))
            .IsTrue();
    }

    [TUnit.Core.Test]
    public async Task CallToolAsync_InvokesPlainTextToolFromAggregatedUpstreamServer()
    {
        await using var primaryServer = await TestMcpServerHost.StartAsync();
        await using var graphServer = await TestMcpServerHost.StartGraphAsync();
        await using var operationsServer = await TestMcpServerHost.StartOperationsAsync();
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(options =>
        {
            options.AddMcpClient("source-a", primaryServer.Client, disposeClient: false);
            options.AddMcpClient("source-b", graphServer.Client, disposeClient: false);
            options.AddMcpClient("source-c", operationsServer.Client, disposeClient: false);
        });

        var result = await gatewayServer.Client.CallToolAsync(
            "source-a:plain_text_search",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["query"] = "hello" }
        );

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Content.Count).IsEqualTo(1);
        await Assert.That(result.Content[0]).IsTypeOf<TextContentBlock>();
        await Assert.That(((TextContentBlock)result.Content[0]).Text).IsEqualTo("plain:hello");
    }

    [TUnit.Core.Test]
    public async Task CallToolAsync_InvokesStructuredToolFromAggregatedUpstreamServer()
    {
        await using var primaryServer = await TestMcpServerHost.StartAsync();
        await using var graphServer = await TestMcpServerHost.StartGraphAsync();
        await using var operationsServer = await TestMcpServerHost.StartOperationsAsync();
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(options =>
        {
            options.AddMcpClient("source-a", primaryServer.Client, disposeClient: false);
            options.AddMcpClient("source-b", graphServer.Client, disposeClient: false);
            options.AddMcpClient("source-c", operationsServer.Client, disposeClient: false);
        });

        var result = await gatewayServer.Client.CallToolAsync(
            "source-b:story_item_detail",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["storyId"] = "story-42" }
        );

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.StructuredContent).IsNotNull();
        await Assert.That(result.Content.Count).IsEqualTo(1);
        await Assert.That(((TextContentBlock)result.Content[0]).Text).Contains("story-42");
    }

    [TUnit.Core.Test]
    public async Task ListPromptsAsync_ExposesAggregatedPromptsFromMultipleUpstreamServers()
    {
        await using var primaryServer = await TestMcpServerHost.StartAsync();
        await using var graphServer = await TestMcpServerHost.StartGraphAsync();
        await using var operationsServer = await TestMcpServerHost.StartOperationsAsync();
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(options =>
        {
            options.AddMcpClient("source-a", primaryServer.Client, disposeClient: false);
            options.AddMcpClient("source-b", graphServer.Client, disposeClient: false);
            options.AddMcpClient("source-c", operationsServer.Client, disposeClient: false);
        });

        var prompts = await gatewayServer.Client.ListPromptsAsync();

        await Assert.That(gatewayServer.Client.ServerCapabilities.Prompts).IsNotNull();
        await Assert.That(prompts.Count).IsEqualTo(4);
        await Assert
            .That(
                prompts.Any(static prompt =>
                    prompt.Name == "source-a:repository_triage_system_prompt"
                )
            )
            .IsTrue();
        await Assert
            .That(
                prompts.Any(static prompt => prompt.Name == "source-b:story_triage_system_prompt")
            )
            .IsTrue();
        await Assert
            .That(
                prompts.Any(static prompt =>
                    prompt.Name == "source-c:deployment_review_system_prompt"
                )
            )
            .IsTrue();
    }

    [TUnit.Core.Test]
    public async Task GetPromptAsync_RendersAggregatedPromptFromSpecificUpstreamServer()
    {
        await using var primaryServer = await TestMcpServerHost.StartAsync();
        await using var graphServer = await TestMcpServerHost.StartGraphAsync();
        await using var operationsServer = await TestMcpServerHost.StartOperationsAsync();
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(options =>
        {
            options.AddMcpClient("source-a", primaryServer.Client, disposeClient: false);
            options.AddMcpClient("source-b", graphServer.Client, disposeClient: false);
            options.AddMcpClient("source-c", operationsServer.Client, disposeClient: false);
        });

        var prompt = await gatewayServer.Client.GetPromptAsync(
            "source-c:deployment_review_system_prompt",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["environment"] = "prod" }
        );

        await Assert.That(prompt.Messages.Count).IsEqualTo(1);
        await Assert.That(prompt.Messages[0].Content).IsTypeOf<TextContentBlock>();
        await Assert.That(((TextContentBlock)prompt.Messages[0].Content).Text).Contains("prod");
    }

    [TUnit.Core.Test]
    public async Task ListResourcesAsync_ExposesAggregatedResourcesFromMultipleUpstreamServers()
    {
        await using var primaryServer = await TestMcpServerHost.StartAsync();
        await using var graphServer = await TestMcpServerHost.StartGraphAsync();
        await using var operationsServer = await TestMcpServerHost.StartOperationsAsync();
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(options =>
        {
            options.AddMcpClient("source-a", primaryServer.Client, disposeClient: false);
            options.AddMcpClient("source-b", graphServer.Client, disposeClient: false);
            options.AddMcpClient("source-c", operationsServer.Client, disposeClient: false);
        });

        var resources = await gatewayServer.Client.ListResourcesAsync();

        await Assert.That(gatewayServer.Client.ServerCapabilities.Resources).IsNotNull();
        await Assert.That(resources.Count).IsEqualTo(3);
        await Assert
            .That(resources.Any(static resource => resource.Name == "source-a:repository_overview"))
            .IsTrue();
        await Assert
            .That(resources.Any(static resource => resource.Name == "source-a:repository_archive"))
            .IsTrue();
        await Assert
            .That(resources.Any(static resource => resource.Name == "source-c:deployment_summary"))
            .IsTrue();
    }

    [TUnit.Core.Test]
    public async Task ListResourceTemplatesAsync_ExposesAggregatedResourceTemplatesFromMultipleUpstreamServers()
    {
        await using var primaryServer = await TestMcpServerHost.StartAsync();
        await using var graphServer = await TestMcpServerHost.StartGraphAsync();
        await using var operationsServer = await TestMcpServerHost.StartOperationsAsync();
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(options =>
        {
            options.AddMcpClient("source-a", primaryServer.Client, disposeClient: false);
            options.AddMcpClient("source-b", graphServer.Client, disposeClient: false);
            options.AddMcpClient("source-c", operationsServer.Client, disposeClient: false);
        });

        var templates = await gatewayServer.Client.ListResourceTemplatesAsync();

        await Assert.That(templates.Count).IsEqualTo(3);
        await Assert
            .That(templates.Any(static template => template.Name == "source-a:issue_detail"))
            .IsTrue();
        await Assert
            .That(templates.Any(static template => template.Name == "source-b:story_context"))
            .IsTrue();
        await Assert
            .That(templates.Any(static template => template.Name == "source-c:runbook_detail"))
            .IsTrue();
    }

    [TUnit.Core.Test]
    public async Task ReadResourceAsync_ReadsBinaryResourceFromAggregatedUpstreamServer()
    {
        await using var primaryServer = await TestMcpServerHost.StartAsync();
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(options =>
        {
            options.AddMcpClient("source-a", primaryServer.Client, disposeClient: false);
        });

        var resource = (await gatewayServer.Client.ListResourcesAsync()).Single(static candidate =>
            candidate.Name == "source-a:repository_archive"
        );
        var readResult = await gatewayServer.Client.ReadResourceAsync(resource.Uri);

        await Assert.That(readResult.Contents.Count).IsEqualTo(1);
        await Assert.That(readResult.Contents[0]).IsTypeOf<BlobResourceContents>();
        await Assert
            .That(((BlobResourceContents)readResult.Contents[0]).DecodedData.ToArray())
            .IsEquivalentTo(new byte[] { 1, 2, 3, 4 });
    }

    [TUnit.Core.Test]
    public async Task ReadResourceAsync_ReadsTemplatedResourceFromAggregatedUpstreamServer()
    {
        await using var primaryServer = await TestMcpServerHost.StartAsync();
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(options =>
        {
            options.AddMcpClient("source-a", primaryServer.Client, disposeClient: false);
        });

        var template = (await gatewayServer.Client.ListResourceTemplatesAsync()).Single(
            static candidate => candidate.Name == "source-a:issue_detail"
        );
        var readResult = await gatewayServer.Client.ReadResourceAsync(
            template.UriTemplate,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = "42" }
        );

        await Assert.That(readResult.Contents.Count).IsEqualTo(1);
        await Assert.That(readResult.Contents[0]).IsTypeOf<TextResourceContents>();
        await Assert
            .That(((TextResourceContents)readResult.Contents[0]).Text)
            .Contains("\"id\":\"42\"");
    }
}
