#pragma warning disable MCPEXP001

using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayLocalToolSourceRegistrationTests
{
    [Test]
    public async Task LoadPromptsAsync_SortsPromptDescriptorsByName()
    {
        var registration = new McpGatewayLocalToolSourceRegistration("local", null);
        registration.AddPrompt(
            new McpGatewayPrompt("zeta_review", BuildPromptAsync) { Description = "Zeta prompt." }
        );
        registration.AddPrompt(
            new McpGatewayPrompt("alpha_review", BuildPromptAsync) { Description = "Alpha prompt." }
        );

        var prompts = await registration.LoadPromptsAsync(
            NullLoggerFactory.Instance,
            CancellationToken.None
        );

        await Assert.That(prompts.Select(static prompt => prompt.Name).ToArray()).IsEquivalentTo(
            ["alpha_review", "zeta_review"]
        );
    }

    [Test]
    public async Task GetPromptAsync_FallsBackToPromptDescriptionAndEmptyServiceProvider()
    {
        var registration = new McpGatewayLocalToolSourceRegistration("local", null);
        registration.AddPrompt(
            new McpGatewayPrompt("release_review", BuildPromptWithoutDescriptionAsync)
            {
                Description = "Release review prompt.",
            }
        );

        var prompt = await registration.GetPromptAsync(
            "release_review",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["repository"] = "ManagedCode/MCPGateway",
            },
            promptContext: null,
            NullLoggerFactory.Instance,
            CancellationToken.None
        );

        await Assert.That(prompt).IsNotNull();
        await Assert.That(prompt!.Description).IsEqualTo("Release review prompt.");
        await Assert.That(((TextContentBlock)prompt.Messages.Single().Content).Text).Contains(
            "ManagedCode/MCPGateway"
        );
    }

    [Test]
    public async Task CompleteAsync_UsesPromptCompletionHandlerAndRejectsUnsupportedReferences()
    {
        var registration = new McpGatewayLocalToolSourceRegistration("local", null);
        registration.AddPrompt(
            new McpGatewayPrompt("release_review", BuildPromptAsync)
            {
                CompleteAsync = CompletePromptAsync,
            }
        );

        var completion = await registration.CompleteAsync(
            new PromptReference { Name = "release_review" },
            new Argument { Name = "repository", Value = "Managed" },
            context: null,
            serviceProvider: null,
            NullLoggerFactory.Instance,
            CancellationToken.None
        );
        var unsupportedReference = await registration.CompleteAsync(
            new ResourceTemplateReference { Uri = "docs://issues/{id}" },
            new Argument { Name = "repository", Value = "Managed" },
            context: null,
            serviceProvider: null,
            NullLoggerFactory.Instance,
            CancellationToken.None
        );
        var missingArgument = await registration.CompleteAsync(
            new PromptReference { Name = "release_review" },
            new Argument { Name = " ", Value = "Managed" },
            context: null,
            serviceProvider: null,
            NullLoggerFactory.Instance,
            CancellationToken.None
        );

        await Assert.That(completion).IsNotNull();
        await Assert.That(completion!.Completion.Values).IsEquivalentTo(
            ["ManagedCode/MCPGateway", "ManagedCode/AIBase"]
        );
        await Assert.That(unsupportedReference).IsNull();
        await Assert.That(missingArgument).IsNull();
    }

    [Test]
    public async Task LoadToolsAsync_UsesOptionalTaskSupportForLocalFunctions()
    {
        var registration = new McpGatewayLocalToolSourceRegistration("local", null);
        registration.AddTool(
            TestFunctionFactory.CreateFunction(
                static (string value) => $"local:{value}",
                "lookup",
                "Looks up a value."
            )
        );

        var loadedTools = await registration.LoadToolsAsync(
            NullLoggerFactory.Instance,
            CancellationToken.None
        );

        await Assert.That(loadedTools.Count).IsEqualTo(1);
        await Assert.That(loadedTools[0].TaskSupport).IsEqualTo(ToolTaskSupport.Optional);
    }

    [Test]
    public async Task BaseDefaults_ReturnEmptyOrNullWhenARegistrationDoesNotOverrideThem()
    {
        var registration = new PassiveRegistration("passive");

        var blankTool = await registration.GetToolAsync(
            " ",
            NullLoggerFactory.Instance,
            CancellationToken.None
        );
        var prompts = await registration.LoadPromptsAsync(
            NullLoggerFactory.Instance,
            CancellationToken.None
        );
        var resources = await registration.LoadResourcesAsync(
            NullLoggerFactory.Instance,
            CancellationToken.None
        );
        var templates = await registration.LoadResourceTemplatesAsync(
            NullLoggerFactory.Instance,
            CancellationToken.None
        );
        var prompt = await registration.GetPromptAsync(
            "release_review",
            arguments: null,
            promptContext: null,
            NullLoggerFactory.Instance,
            CancellationToken.None
        );
        var resource = await registration.ReadResourceAsync(
            "docs://overview",
            NullLoggerFactory.Instance,
            CancellationToken.None
        );
        var completion = await registration.CompleteAsync(
            new PromptReference { Name = "release_review" },
            new Argument { Name = "repository", Value = "Managed" },
            context: null,
            serviceProvider: null,
            NullLoggerFactory.Instance,
            CancellationToken.None
        );
        var promptSubscription = await registration.SubscribeToPromptListChangesAsync(
            static (_, _) => ValueTask.CompletedTask,
            NullLoggerFactory.Instance,
            CancellationToken.None
        );
        var task = await registration.CallToolAsTaskAsync(
            "lookup",
            arguments: null,
            new McpTaskMetadata(),
            progress: null,
            NullLoggerFactory.Instance,
            CancellationToken.None
        );
        var trackedTask = await registration.GetTaskAsync(
            "task-id",
            NullLoggerFactory.Instance,
            CancellationToken.None
        );
        var taskResult = await registration.GetTaskResultAsync(
            "task-id",
            NullLoggerFactory.Instance,
            CancellationToken.None
        );
        var cancelledTask = await registration.CancelTaskAsync(
            "task-id",
            NullLoggerFactory.Instance,
            CancellationToken.None
        );
        var resourceSubscription = await registration.SubscribeToResourceAsync(
            "docs://overview",
            static (_, _) => ValueTask.CompletedTask,
            NullLoggerFactory.Instance,
            CancellationToken.None
        );
        var taskSubscription = await registration.SubscribeToTaskStatusAsync(
            "task-id",
            static (_, _) => ValueTask.CompletedTask,
            NullLoggerFactory.Instance,
            CancellationToken.None
        );

        await Assert.That(blankTool).IsNull();
        await Assert.That(prompts.Count).IsEqualTo(0);
        await Assert.That(resources.Count).IsEqualTo(0);
        await Assert.That(templates.Count).IsEqualTo(0);
        await Assert.That(prompt).IsNull();
        await Assert.That(resource).IsNull();
        await Assert.That(completion).IsNull();
        await Assert.That(promptSubscription).IsNull();
        await Assert.That(task).IsNull();
        await Assert.That(trackedTask).IsNull();
        await Assert.That(taskResult).IsNull();
        await Assert.That(cancelledTask).IsNull();
        await Assert.That(resourceSubscription).IsNull();
        await Assert.That(taskSubscription).IsNull();
    }

    private static ValueTask<GetPromptResult> BuildPromptAsync(
        McpGatewayPromptRenderContext context,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(
            new GetPromptResult
            {
                Description = "Explicit description",
                Messages =
                [
                    new PromptMessage
                    {
                        Role = Role.User,
                        Content = new TextContentBlock { Text = context.PromptName },
                    },
                ],
            }
        );
    }

    private static ValueTask<GetPromptResult> BuildPromptWithoutDescriptionAsync(
        McpGatewayPromptRenderContext context,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = context.Services.GetService(typeof(string));

        return ValueTask.FromResult(
            new GetPromptResult
            {
                Messages =
                [
                    new PromptMessage
                    {
                        Role = Role.User,
                        Content = new TextContentBlock
                        {
                            Text = context.Arguments["repository"]?.ToString() ?? string.Empty,
                        },
                    },
                ],
            }
        );
    }

    private static ValueTask<CompleteResult?> CompletePromptAsync(
        McpGatewayPromptCompletionContext context,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult<CompleteResult?>(
            new CompleteResult
            {
                Completion = new Completion
                {
                    Values = ["ManagedCode/MCPGateway", "ManagedCode/AIBase"],
                    Total = 2,
                },
            }
        );
    }

    private sealed class PassiveRegistration(string sourceId)
        : McpGatewayToolSourceRegistration(sourceId, null)
    {
        public override McpGatewaySourceRegistrationKind Kind => McpGatewaySourceRegistrationKind.Local;

        public override ValueTask<IReadOnlyList<McpGatewayLoadedTool>> LoadToolsAsync(
            Microsoft.Extensions.Logging.ILoggerFactory loggerFactory,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult<IReadOnlyList<McpGatewayLoadedTool>>([]);
    }
}

#pragma warning restore MCPEXP001
