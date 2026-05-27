#pragma warning disable MCPEXP001

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayOptionsConfigurationTests
{
    [Test]
    public async Task RegistrationMethods_AddExpectedSourceRegistrations()
    {
        await using var upstreamServer = await TestMcpServerHost.StartAsync();
        var prompt = new McpGatewayPrompt("release_review", BuildPromptAsync)
        {
            Description = "Builds a release review prompt.",
        };
        var options = new McpGatewayOptions()
            .AddTool(
                "local",
                TestFunctionFactory.CreateFunction(
                    static (string value) => $"local:{value}",
                    "local_lookup",
                    "Looks up a local value."
                )
            )
            .AddPrompt("local", prompt)
            .AddHttpServer(
                "http-source",
                new Uri("https://example.com/mcp"),
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Authorization"] = "Bearer token",
                }
            )
            .AddStdioServer(
                "stdio-source",
                "dotnet",
                ["run", "--project", "sample.csproj"],
                "/tmp/mcp",
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["DOTNET_ENVIRONMENT"] = "Development",
                }
            )
            .AddMcpClient("provided-client", upstreamServer.Client, disposeClient: false)
            .AddMcpClientFactory(
                "factory-client",
                static _ => ValueTask.FromException<ModelContextProtocol.Client.McpClient>(
                    new InvalidOperationException("Factory is not executed during registration.")
                ),
                disposeClient: false
            );

        var registrations = options.SourceRegistrations;
        var localRegistration = registrations.OfType<McpGatewayLocalToolSourceRegistration>().Single();
        var registrationKinds = registrations.Select(static registration => registration.Kind).ToArray();
        var loadedTools = await localRegistration.LoadToolsAsync(
            NullLoggerFactory.Instance,
            CancellationToken.None
        );
        var loadedPrompts = await localRegistration.LoadPromptsAsync(
            NullLoggerFactory.Instance,
            CancellationToken.None
        );

        await Assert.That(registrations.Count).IsEqualTo(5);
        await Assert
            .That(registrationKinds)
            .IsEquivalentTo(
                [
                    McpGatewaySourceRegistrationKind.Local,
                    McpGatewaySourceRegistrationKind.Http,
                    McpGatewaySourceRegistrationKind.Stdio,
                    McpGatewaySourceRegistrationKind.CustomMcpClient,
                    McpGatewaySourceRegistrationKind.CustomMcpClient,
                ]
            );
        await Assert.That(loadedTools.Count).IsEqualTo(1);
        await Assert.That(loadedTools[0].TaskSupport).IsEqualTo(ModelContextProtocol.Protocol.ToolTaskSupport.Optional);
        await Assert.That(loadedPrompts.Count).IsEqualTo(1);
        await Assert.That(loadedPrompts[0].Name).IsEqualTo("release_review");
    }

    [Test]
    public async Task AddHttpServer_WithOptionsObject_AddsHttpSourceRegistration()
    {
        var options = new McpGatewayOptions().AddHttpServer(
            new McpGatewayHttpServerOptions
            {
                SourceId = "http-source",
                Endpoint = new Uri("https://example.com/mcp"),
                DisplayName = "HTTP source",
                ConnectionTimeout = TimeSpan.FromSeconds(10),
                MaxReconnectionAttempts = 1,
            }
        );

        var registration = options.SourceRegistrations.Single();

        await Assert.That(registration.Kind).IsEqualTo(McpGatewaySourceRegistrationKind.Http);
        await Assert.That(registration.SourceId).IsEqualTo("http-source");
        await Assert.That(registration.DisplayName).IsEqualTo("HTTP source");
    }

    [Test]
    public async Task MarkdownLdGraphConfigurationMethods_SwapModesAndFactories()
    {
        var staticDocuments = new[]
        {
            new McpGatewayMarkdownLdGraphDocument(
                "docs/release-review.md",
                "# Release review"
            ),
        };
        var options = new McpGatewayOptions()
            .UseMarkdownLdGraphDocuments(staticDocuments)
            .UseMarkdownLdGraphDocuments(
                static descriptors =>
                [
                    new McpGatewayMarkdownLdGraphDocument(
                        "docs/generated.md",
                        $"# {descriptors.Count}"
                    ),
                ]
            );

        var generatedDocuments =
            await options.MarkdownLdGraphDocumentFactory!(
                [
                    new McpGatewayToolDescriptor(
                        "local:lookup",
                        "local",
                        McpGatewaySourceKind.Local,
                        new Tool
                        {
                            Name = "lookup",
                            Title = "Lookup",
                            Description = "Looks up data.",
                            InputSchema = JsonSerializer.SerializeToElement(
                                new { type = "object" },
                                McpGatewayJsonSerializer.Options
                            ),
                        },
                        ["value"]
                    ),
                ],
                CancellationToken.None
            );

        options.UseMarkdownLdGraphFile("/tmp/graph.json");

        await Assert.That(generatedDocuments.Count).IsEqualTo(1);
        await Assert.That(generatedDocuments[0].Path).IsEqualTo("docs/generated.md");
        await Assert.That(options.MarkdownLdGraphSource).IsEqualTo(
            McpGatewayMarkdownLdGraphSource.FileSystem
        );
        await Assert.That(options.MarkdownLdGraphPath).IsEqualTo("/tmp/graph.json");
        await Assert.That(options.MarkdownLdGraphDocumentFactory is null).IsTrue();

        options.UseGeneratedMarkdownLdGraph();

        await Assert.That(options.MarkdownLdGraphSource).IsEqualTo(
            McpGatewayMarkdownLdGraphSource.GeneratedToolGraph
        );
        await Assert.That(options.MarkdownLdGraphPath).IsNull();
        await Assert.That(options.MarkdownLdGraphDocumentFactory is null).IsTrue();
    }

    [Test]
    public async Task OverloadMethods_RegisterToolsAndPromptsAcrossAllConvenienceShapes()
    {
        var searchHints = new McpGatewayToolSearchHints(["lookup"], ["ops"]);
        var options = new McpGatewayOptions()
            .AddTool(
                "alpha",
                TestFunctionFactory.CreateFunction(
                    static () => "alpha",
                    "alpha_tool",
                    "Returns alpha."
                ),
                searchHints
            )
            .AddTool(
                TestFunctionFactory.CreateFunction(
                    static () => "beta",
                    "beta_tool",
                    "Returns beta."
                ),
                searchHints,
                "beta"
            )
            .AddTools(
                "gamma",
                [
                    TestFunctionFactory.CreateFunction(
                        static () => "gamma-a",
                        "gamma_a",
                        "Returns gamma-a."
                    ),
                    TestFunctionFactory.CreateFunction(
                        static () => "gamma-b",
                        "gamma_b",
                        "Returns gamma-b."
                    ),
                ]
            )
            .AddTools(
                [
                    TestFunctionFactory.CreateFunction(
                        static () => "delta-a",
                        "delta_a",
                        "Returns delta-a."
                    ),
                ],
                "delta"
            )
            .AddPrompt(new McpGatewayPrompt("default_prompt", BuildPromptAsync))
            .AddPrompts(
                "epsilon",
                [new McpGatewayPrompt("epsilon_prompt", BuildPromptAsync)]
            )
            .AddPrompts([new McpGatewayPrompt("zeta_prompt", BuildPromptAsync)], "zeta");

        var registrations = options.SourceRegistrations.OfType<McpGatewayLocalToolSourceRegistration>().ToArray();
        var loadedTools = await Task.WhenAll(
            registrations.Select(registration =>
                registration.LoadToolsAsync(NullLoggerFactory.Instance, CancellationToken.None).AsTask()
            )
        );
        var loadedPrompts = await Task.WhenAll(
            registrations.Select(registration =>
                registration.LoadPromptsAsync(NullLoggerFactory.Instance, CancellationToken.None).AsTask()
            )
        );

        await Assert.That(registrations.Length).IsEqualTo(7);
        await Assert.That(loadedTools.Sum(static tools => tools.Count)).IsEqualTo(5);
        await Assert.That(loadedPrompts.Sum(static prompts => prompts.Count)).IsEqualTo(3);
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
                            Text = $"Review '{context.PromptName}'.",
                        },
                    },
                ],
            }
        );
    }
}

#pragma warning restore MCPEXP001
