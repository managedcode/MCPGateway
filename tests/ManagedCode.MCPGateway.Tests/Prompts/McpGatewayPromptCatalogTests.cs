using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

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
        await Assert.That(prompt.Messages[0].Role).IsEqualTo(Role.User);
        await Assert.That(ReadText(prompt.Messages[0])).Contains("ManagedCode.MCPGateway");
        await Assert.That(ReadText(prompt.Messages[0])).Contains("uk");
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

    [TUnit.Core.Test]
    public async Task ListPromptsAsync_IncludesGatewayOwnedPromptDescriptors()
    {
        await using var repositoryServer = await TestMcpServerHost.StartAsync();
        await using var operationsServer = await TestMcpServerHost.StartOperationsAsync();
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            ConfigureCompositePromptCatalog(options, repositoryServer, operationsServer);
        });
        var promptCatalog = serviceProvider.GetRequiredService<IMcpGatewayPromptCatalog>();

        var prompts = await promptCatalog.ListPromptsAsync();
        var descriptor = prompts.Single(static prompt =>
            prompt.PromptId == "local:release_review_bundle"
        );

        await Assert.That(descriptor.SourceKind).IsEqualTo(McpGatewaySourceKind.Local);
        await Assert.That(descriptor.DisplayName).IsEqualTo("Release review bundle");
        await Assert.That(descriptor.RequiredArguments).IsEquivalentTo(["repository", "environment"]);
        await Assert
            .That(descriptor.Arguments.Select(static argument => argument.Name).ToArray())
            .IsEquivalentTo(["repository", "environment", "locale"]);
    }

    [TUnit.Core.Test]
    public async Task GetPromptAsync_ComposesGatewayOwnedPromptAcrossMultipleSources()
    {
        await using var repositoryServer = await TestMcpServerHost.StartAsync();
        await using var operationsServer = await TestMcpServerHost.StartOperationsAsync();
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            ConfigureCompositePromptCatalog(options, repositoryServer, operationsServer);
        });
        var promptCatalog = serviceProvider.GetRequiredService<IMcpGatewayPromptCatalog>();

        var prompt = await promptCatalog.GetPromptAsync(
            new McpGatewayPromptRequest(
                SourceId: "local",
                PromptName: "release_review_bundle",
                Arguments: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["repository"] = "ManagedCode/MCPGateway",
                    ["environment"] = "prod-eu",
                    ["locale"] = "uk-UA",
                }
            )
        );

        await Assert.That(prompt).IsNotNull();
        await Assert.That(prompt!.PromptId).IsEqualTo("local:release_review_bundle");
        await Assert.That(prompt.Messages.Count).IsEqualTo(4);
        await Assert.That(ReadText(prompt.Messages[0]))
            .Contains("Combine repository and deployment guidance");
        await Assert.That(ReadText(prompt.Messages[1])).Contains("ManagedCode/MCPGateway");
        await Assert.That(ReadText(prompt.Messages[2])).Contains("prod-eu");
        await Assert.That(ReadText(prompt.Messages[3])).Contains("uk-UA");
    }

    private static void ConfigureCompositePromptCatalog(
        McpGatewayOptions options,
        TestMcpServerHost repositoryServer,
        TestMcpServerHost operationsServer
    )
    {
        options.AddMcpClient("repo", repositoryServer.Client, disposeClient: false);
        options.AddMcpClient("ops", operationsServer.Client, disposeClient: false);
        options.AddPrompt(
            new McpGatewayPrompt("release_review_bundle", BuildCompositePromptAsync)
            {
                DisplayName = "Release review bundle",
                Description =
                    "Combines repository triage and deployment review guidance into one prompt.",
                Arguments =
                [
                    new McpGatewayPromptArgumentDescriptor(
                        "repository",
                        "Repository",
                        "Repository name.",
                        true
                    ),
                    new McpGatewayPromptArgumentDescriptor(
                        "environment",
                        "Environment",
                        "Deployment environment.",
                        true
                    ),
                    new McpGatewayPromptArgumentDescriptor(
                        "locale",
                        "Locale",
                        "Preferred review locale.",
                        false
                    ),
                ],
                CompleteAsync = CompleteCompositePromptAsync,
            }
        );
    }

    private static async ValueTask<GetPromptResult> BuildCompositePromptAsync(
        McpGatewayPromptRenderContext context,
        CancellationToken cancellationToken
    )
    {
        var repository = context.Arguments["repository"]?.ToString() ?? string.Empty;
        var environment = context.Arguments["environment"]?.ToString() ?? string.Empty;
        var locale = context.Arguments.TryGetValue("locale", out var rawLocale)
            ? rawLocale?.ToString() ?? "en-US"
            : "en-US";

        var repositoryPrompt =
            await context.GetPromptAsync(
                "repo",
                "repository_triage_system_prompt",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["repository"] = repository,
                    ["locale"] = locale,
                },
                cancellationToken
            ) ?? throw new InvalidOperationException("Repository prompt was not found.");

        var deploymentPrompt =
            await context.GetPromptAsync(
                "ops",
                "deployment_review_system_prompt",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["environment"] = environment,
                },
                cancellationToken
            ) ?? throw new InvalidOperationException("Deployment prompt was not found.");

        return new GetPromptResult
        {
            Description = "Release review bundle prompt.",
            Messages =
            [
                new PromptMessage
                {
                    Role = Role.User,
                    Content = new TextContentBlock
                    {
                        Text = "Combine repository and deployment guidance into one review plan.",
                    },
                },
                ..repositoryPrompt.Messages,
                ..deploymentPrompt.Messages,
                new PromptMessage
                {
                    Role = Role.User,
                    Content = new TextContentBlock
                    {
                        Text = $"Finalize the review in locale '{locale}'.",
                    },
                },
            ],
        };
    }

    private static ValueTask<CompleteResult?> CompleteCompositePromptAsync(
        McpGatewayPromptCompletionContext context,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        var values = context.ArgumentName switch
        {
            "repository" => new[]
            {
                "ManagedCode/MCPGateway",
                "ManagedCode/AIBase",
                "ModelContextProtocol/csharp-sdk",
            },
            "environment" => new[] { "prod-eu", "prod-us", "staging" },
            _ => [],
        };

        var matches = values
            .Where(value =>
                value.StartsWith(context.ArgumentValue, StringComparison.OrdinalIgnoreCase)
            )
            .ToList();

        return ValueTask.FromResult<CompleteResult?>(
            new CompleteResult
            {
                Completion = new Completion
                {
                    Values = matches,
                    Total = matches.Count,
                    HasMore = false,
                },
            }
        );
    }

    private static string? ReadText(PromptMessage message) =>
        message.Content is TextContentBlock textContent ? textContent.Text : null;
}
