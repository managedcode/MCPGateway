using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayMcpServerPromptFeatureIntegrationTests
{
    [Test]
    public async Task GetPromptAsync_ExportsGatewayOwnedCompositePrompt()
    {
        await using var repositoryServer = await TestMcpServerHost.StartAsync();
        await using var operationsServer = await TestMcpServerHost.StartOperationsAsync();
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(options =>
        {
            ConfigureCompositePromptCatalog(options, repositoryServer, operationsServer);
        });

        var prompt = await gatewayServer.Client.GetPromptAsync(
            "local:release_review_bundle",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["repository"] = "ManagedCode/MCPGateway",
                ["environment"] = "prod-eu",
                ["locale"] = "uk-UA",
            }
        );

        await Assert.That(prompt.Messages.Count).IsEqualTo(4);
        await Assert.That(prompt.Messages[0].Content).IsTypeOf<TextContentBlock>();
        await Assert
            .That(((TextContentBlock)prompt.Messages[0].Content).Text)
            .Contains("Combine repository and deployment guidance");
        await Assert
            .That(((TextContentBlock)prompt.Messages[1].Content).Text)
            .Contains("ManagedCode/MCPGateway");
        await Assert
            .That(((TextContentBlock)prompt.Messages[2].Content).Text)
            .Contains("prod-eu");
        await Assert
            .That(((TextContentBlock)prompt.Messages[3].Content).Text)
            .Contains("uk-UA");
    }

    [Test]
    public async Task CompleteAsync_CompletesGatewayOwnedPromptArgument()
    {
        await using var repositoryServer = await TestMcpServerHost.StartAsync();
        await using var operationsServer = await TestMcpServerHost.StartOperationsAsync();
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(options =>
        {
            ConfigureCompositePromptCatalog(options, repositoryServer, operationsServer);
        });

        var completion = await gatewayServer.Client.CompleteAsync(
            new PromptReference { Name = "local:release_review_bundle" },
            "repository",
            "Managed"
        );

        await Assert.That(completion.Completion.Values).IsEquivalentTo(
            ["ManagedCode/MCPGateway", "ManagedCode/AIBase"]
        );
        await Assert.That(completion.Completion.Total).IsEqualTo(2);
    }

    [Test]
    public async Task PromptListChangedNotification_IsRaisedWhenGatewayRegistryAddsPrompt()
    {
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(static _ => { });
        var changed = new TaskCompletionSource<PromptListChangedNotificationParams>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        await using var notificationRegistration = gatewayServer.Client.RegisterNotificationHandler(
            NotificationMethods.PromptListChangedNotification,
            (notification, _) =>
            {
                changed.TrySetResult(new PromptListChangedNotificationParams());
                return ValueTask.CompletedTask;
            }
        );

        _ = await gatewayServer.Client.ListPromptsAsync();
        gatewayServer.Registry.AddPrompt(
            new McpGatewayPrompt("hotfix_review_bundle", BuildSimplePromptAsync)
            {
                DisplayName = "Hotfix review bundle",
                Description = "Builds a simple hotfix review prompt.",
            }
        );

        _ = await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var prompts = await gatewayServer.Client.ListPromptsAsync();

        await Assert.That(gatewayServer.Client.ServerCapabilities.Prompts?.ListChanged).IsTrue();
        await Assert
            .That(prompts.Any(static prompt => prompt.Name == "local:hotfix_review_bundle"))
            .IsTrue();
    }

    [Test]
    public async Task PromptListChangedNotification_ForwardsUpstreamPromptListChanges()
    {
        await using var upstreamServer = await TestMcpPromptListFeatureServerHost.StartAsync();
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(options =>
        {
            options.AddMcpClient("source-a", upstreamServer.Client, disposeClient: false);
        });
        var changed = new TaskCompletionSource<PromptListChangedNotificationParams>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        await using var notificationRegistration = gatewayServer.Client.RegisterNotificationHandler(
            NotificationMethods.PromptListChangedNotification,
            (notification, _) =>
            {
                changed.TrySetResult(new PromptListChangedNotificationParams());
                return ValueTask.CompletedTask;
            }
        );

        _ = await gatewayServer.Client.ListPromptsAsync();
        await upstreamServer.AddPromptAsync("deployment_review_prompt");

        _ = await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var prompts = await gatewayServer.Client.ListPromptsAsync();

        await Assert
            .That(prompts.Any(static prompt => prompt.Name == "source-a:deployment_review_prompt"))
            .IsTrue();
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

    private static ValueTask<GetPromptResult> BuildSimplePromptAsync(
        McpGatewayPromptRenderContext context,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(
            new GetPromptResult
            {
                Description = "Hotfix review bundle prompt.",
                Messages =
                [
                    new PromptMessage
                    {
                        Role = Role.User,
                        Content = new TextContentBlock
                        {
                            Text = "Review the hotfix rollout and verify rollback readiness.",
                        },
                    },
                ],
            }
        );
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
}
