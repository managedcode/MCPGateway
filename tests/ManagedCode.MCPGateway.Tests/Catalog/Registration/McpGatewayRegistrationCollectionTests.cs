using Microsoft.Extensions.Logging.Abstractions;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayRegistrationCollectionTests
{
    [Test]
    public async Task AddPrompt_ThrowsWhenPromptNameIsDuplicatedWithinSource()
    {
        var collection = new McpGatewayRegistrationCollection();
        collection.AddPrompt(
            new McpGatewayPrompt("release_review", BuildPromptAsync),
            "local"
        );

        InvalidOperationException? exception = null;
        try
        {
            collection.AddPrompt(
                new McpGatewayPrompt("release_review", BuildPromptAsync),
                "local"
            );
        }
        catch (InvalidOperationException ex)
        {
            exception = ex;
        }

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.Message).Contains("already registered");
    }

    [Test]
    public async Task Validation_RejectsMissingSourceIdsAndCommands()
    {
        var collection = new McpGatewayRegistrationCollection();

        var missingSourceId = Capture(() =>
            collection.AddHttpServer(" ", new Uri("https://example.com/mcp"))
        );
        var missingCommand = Capture(() =>
            collection.AddStdioServer("stdio", " ")
        );

        await Assert.That(missingSourceId).IsNotNull();
        await Assert.That(missingSourceId!.Message).Contains("source id");
        await Assert.That(missingCommand).IsNotNull();
        await Assert.That(missingCommand!.Message).Contains("command");
    }

    [Test]
    public async Task Drain_ClearsLocalRegistrationsAndAllowsFreshRecreation()
    {
        var collection = new McpGatewayRegistrationCollection();
        collection.AddTool(
            TestFunctionFactory.CreateFunction(
                static () => "ok",
                "first_tool",
                "Returns a value."
            ),
            "local"
        );

        var initialRegistration = collection
            .Snapshot()
            .OfType<McpGatewayLocalToolSourceRegistration>()
            .Single();
        var drained = collection.Drain();

        collection.AddPrompt(
            new McpGatewayPrompt("release_review", BuildPromptAsync),
            "local"
        );

        var recreatedRegistration = collection
            .Snapshot()
            .OfType<McpGatewayLocalToolSourceRegistration>()
            .Single();
        var loadedPrompts = await recreatedRegistration.LoadPromptsAsync(
            NullLoggerFactory.Instance,
            CancellationToken.None
        );

        await Assert.That(drained.Count).IsEqualTo(1);
        await Assert.That(ReferenceEquals(initialRegistration, recreatedRegistration)).IsFalse();
        await Assert.That(loadedPrompts.Count).IsEqualTo(1);
        await Assert.That(loadedPrompts[0].Name).IsEqualTo("release_review");
    }

    [Test]
    public async Task ExistingLocalRegistration_IsReusedWhenCollectionStartsFromSnapshot()
    {
        var originalCollection = new McpGatewayRegistrationCollection();
        originalCollection.AddTool(
            TestFunctionFactory.CreateFunction(
                static () => "ok",
                "first_tool",
                "Returns a value."
            ),
            "local"
        );

        var seededCollection = new McpGatewayRegistrationCollection(originalCollection.Snapshot());
        seededCollection.AddTool(
            TestFunctionFactory.CreateFunction(
                static () => "next",
                "second_tool",
                "Returns another value."
            ),
            "local"
        );

        var registrations = seededCollection.Snapshot().OfType<McpGatewayLocalToolSourceRegistration>().ToArray();
        var tools = await registrations[0].LoadToolsAsync(
            NullLoggerFactory.Instance,
            CancellationToken.None
        );

        await Assert.That(registrations.Length).IsEqualTo(1);
        await Assert.That(tools.Select(static tool => tool.Tool.Name).ToArray()).IsEquivalentTo(
            ["first_tool", "second_tool"]
        );
    }

    private static ArgumentException? Capture(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (ArgumentException ex)
        {
            return ex;
        }
    }

    private static ValueTask<ModelContextProtocol.Protocol.GetPromptResult> BuildPromptAsync(
        McpGatewayPromptRenderContext context,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(
            new ModelContextProtocol.Protocol.GetPromptResult
            {
                Messages =
                [
                    new ModelContextProtocol.Protocol.PromptMessage
                    {
                        Role = ModelContextProtocol.Protocol.Role.User,
                        Content = new ModelContextProtocol.Protocol.TextContentBlock
                        {
                            Text = context.PromptName,
                        },
                    },
                ],
            }
        );
    }
}
