using System.ComponentModel;
using System.Text.Json;

using ManagedCode.MCPGateway.Abstractions;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayInvocationTests
{
    [TUnit.Core.Test]
    public async Task InvokeAsync_InvokesLocalFunctionAndMapsQueryArgument()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddTool(
                "local",
                CreateFunction(TextUppercase, "text_uppercase", "Convert query text to uppercase."));
        });

        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
        await gateway.BuildIndexAsync();

        var invokeResult = await gateway.InvokeAsync(new McpGatewayInvokeRequest(
            ToolId: "local:text_uppercase",
            Query: "hello gateway"));

        await Assert.That(invokeResult.IsSuccess).IsTrue();
        await Assert.That(invokeResult.Output).IsTypeOf<string>();
        await Assert.That((string)invokeResult.Output!).IsEqualTo("HELLO GATEWAY");
    }

    [TUnit.Core.Test]
    public async Task InvokeAsync_MapsQueryArgumentWhenSchemaMarksItOptional()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddTool(
                "local",
                CreateFunction(OptionalQueryEcho, "optional_query_echo", "Echo optional query text in uppercase."));
        });

        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
        await gateway.BuildIndexAsync();

        var invokeResult = await gateway.InvokeAsync(new McpGatewayInvokeRequest(
            ToolId: "local:optional_query_echo",
            Query: "hello gateway"));

        await Assert.That(invokeResult.IsSuccess).IsTrue();
        await Assert.That(invokeResult.Output).IsTypeOf<string>();
        await Assert.That((string)invokeResult.Output!).IsEqualTo("HELLO GATEWAY");
    }

    [TUnit.Core.Test]
    public async Task InvokeAsync_MapsContextSummaryToRequiredLocalArguments()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddTool(
                "local",
                CreateFunction(EchoContextSummary, "context_summary_echo", "Echo query and context summary."));
        });

        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
        await gateway.BuildIndexAsync();

        var invokeResult = await gateway.InvokeAsync(new McpGatewayInvokeRequest(
            ToolId: "local:context_summary_echo",
            Query: "open github",
            ContextSummary: "user is on repository settings page"));

        await Assert.That(invokeResult.IsSuccess).IsTrue();
        await Assert.That((string)invokeResult.Output!).IsEqualTo("open github|user is on repository settings page");
    }

    [TUnit.Core.Test]
    public async Task InvokeAsync_MapsStructuredContextToRequiredLocalArguments()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddTool(
                "local",
                CreateFunction(ReadStructuredContext, "structured_context_echo", "Read structured context payload."));
        });

        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
        await gateway.BuildIndexAsync();

        var invokeResult = await gateway.InvokeAsync(new McpGatewayInvokeRequest(
            ToolId: "local:structured_context_echo",
            Context: new Dictionary<string, object?>
            {
                ["domain"] = "genealogy",
                ["page"] = "tree-profile"
            }));

        await Assert.That(invokeResult.IsSuccess).IsTrue();
        await Assert.That((string)invokeResult.Output!).IsEqualTo("genealogy|tree-profile");
    }

    [TUnit.Core.Test]
    public async Task InvokeAsync_PrefersExplicitArgumentsOverMappedValues()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddTool(
                "local",
                CreateFunction(EchoContextSummary, "context_summary_echo", "Echo query and context summary."));
        });

        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
        await gateway.BuildIndexAsync();

        var invokeResult = await gateway.InvokeAsync(new McpGatewayInvokeRequest(
            ToolId: "local:context_summary_echo",
            Query: "mapped query",
            ContextSummary: "mapped summary",
            Arguments: new Dictionary<string, object?>
            {
                ["query"] = "explicit query",
                ["contextSummary"] = "explicit summary"
            }));

        await Assert.That(invokeResult.IsSuccess).IsTrue();
        await Assert.That((string)invokeResult.Output!).IsEqualTo("explicit query|explicit summary");
    }

    [TUnit.Core.Test]
    public async Task InvokeAsync_ResolvesByToolNameAndSourceId()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddTool("alpha", CreateFunction(AlphaSharedSearch, "shared_search", "Alpha search tool."));
            options.AddTool("beta", CreateFunction(BetaSharedSearch, "shared_search", "Beta search tool."));
        });

        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
        await gateway.BuildIndexAsync();

        var invokeResult = await gateway.InvokeAsync(new McpGatewayInvokeRequest(
            ToolName: "shared_search",
            SourceId: "beta",
            Query: "hello"));

        await Assert.That(invokeResult.IsSuccess).IsTrue();
        await Assert.That((string)invokeResult.Output!).IsEqualTo("beta:hello");
    }

    [TUnit.Core.Test]
    public async Task InvokeAsync_ReturnsAmbiguousErrorWhenToolNameExistsInMultipleSources()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddTool("alpha", CreateFunction(AlphaSharedSearch, "shared_search", "Alpha search tool."));
            options.AddTool("beta", CreateFunction(BetaSharedSearch, "shared_search", "Beta search tool."));
        });

        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
        await gateway.BuildIndexAsync();

        var invokeResult = await gateway.InvokeAsync(new McpGatewayInvokeRequest(
            ToolName: "shared_search",
            Query: "hello"));

        await Assert.That(invokeResult.IsSuccess).IsFalse();
        await Assert.That(invokeResult.Error!.Contains("ambiguous", StringComparison.OrdinalIgnoreCase)).IsTrue();
    }

    [TUnit.Core.Test]
    public async Task InvokeAsync_ReturnsNotFoundWhenToolDoesNotExist()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddTool("local", CreateFunction(TextUppercase, "text_uppercase", "Convert query text to uppercase."));
        });

        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
        await gateway.BuildIndexAsync();

        var invokeResult = await gateway.InvokeAsync(new McpGatewayInvokeRequest(
            ToolId: "local:missing_tool"));

        await Assert.That(invokeResult.IsSuccess).IsFalse();
        await Assert.That(invokeResult.Error!.Contains("was not found", StringComparison.OrdinalIgnoreCase)).IsTrue();
    }

    [TUnit.Core.Test]
    public async Task InvokeAsync_NormalizesJsonScalarOutputs()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddTool("local", CreateFunction(ReturnJsonString, "json_string_result", "Return a JSON string scalar."));
        });

        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
        await gateway.BuildIndexAsync();

        var invokeResult = await gateway.InvokeAsync(new McpGatewayInvokeRequest(
            ToolId: "local:json_string_result"));

        await Assert.That(invokeResult.IsSuccess).IsTrue();
        await Assert.That(invokeResult.Output).IsTypeOf<string>();
        await Assert.That((string)invokeResult.Output!).IsEqualTo("done");
    }

    [TUnit.Core.Test]
    public async Task InvokeAsync_ReturnsFailureWhenLocalFunctionThrows()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddTool("local", CreateFunction(ThrowingTool, "throwing_tool", "Throw an exception for test coverage."));
        });

        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
        await gateway.BuildIndexAsync();

        var invokeResult = await gateway.InvokeAsync(new McpGatewayInvokeRequest(
            ToolId: "local:throwing_tool"));

        await Assert.That(invokeResult.IsSuccess).IsFalse();
        await Assert.That(invokeResult.Error).IsEqualTo("boom");
    }

    [TUnit.Core.Test]
    public async Task InvokeAsync_InvokesStructuredMcpToolAndMapsQueryArgument()
    {
        await using var serverHost = await TestMcpServerHost.StartAsync();
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddMcpClient("test-mcp", serverHost.Client, disposeClient: false);
        });

        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
        await gateway.BuildIndexAsync();

        var invokeResult = await gateway.InvokeAsync(new McpGatewayInvokeRequest(
            ToolId: "test-mcp:github_repository_search",
            Query: "managedcode"));

        await Assert.That(invokeResult.IsSuccess).IsTrue();
        await Assert.That(invokeResult.Output).IsTypeOf<JsonElement>();

        var output = (JsonElement)invokeResult.Output!;
        await Assert.That(GetJsonProperty(output, "query").GetString()).IsEqualTo("managedcode");
        await Assert.That(GetJsonProperty(output, "source").GetString()).IsEqualTo("mcp");
    }

    [TUnit.Core.Test]
    public async Task InvokeAsync_PassesContextMetaToMcpToolRequests()
    {
        await using var serverHost = await TestMcpServerHost.StartAsync();
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddMcpClient("test-mcp", serverHost.Client, disposeClient: false);
        });

        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
        await gateway.BuildIndexAsync();

        var invokeResult = await gateway.InvokeAsync(new McpGatewayInvokeRequest(
            ToolId: "test-mcp:github_repository_search",
            Query: "managedcode",
            ContextSummary: "user is on repository settings page",
            Context: new Dictionary<string, object?>
            {
                ["page"] = "settings",
                ["domain"] = "github"
            }));

        await Assert.That(invokeResult.IsSuccess).IsTrue();
        await Assert.That(serverHost.CapturedMeta.Count > 0).IsTrue();

        var payload = serverHost.CapturedMeta[^1];
        await Assert.That(payload.TryGetPropertyValue("managedCodeMcpGateway", out var gatewayNode)).IsTrue();

        var gatewayMeta = gatewayNode!.AsObject();
        await Assert.That(gatewayMeta["query"]!.GetValue<string>()).IsEqualTo("managedcode");
        await Assert.That(gatewayMeta["contextSummary"]!.GetValue<string>()).IsEqualTo("user is on repository settings page");
        await Assert.That(gatewayMeta["context"]!["page"]!.GetValue<string>()).IsEqualTo("settings");
        await Assert.That(gatewayMeta["context"]!["domain"]!.GetValue<string>()).IsEqualTo("github");
    }

    [TUnit.Core.Test]
    public async Task InvokeAsync_ParsesJsonTextContentFromMcpTool()
    {
        await using var serverHost = await TestMcpServerHost.StartAsync();
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddMcpClient("test-mcp", serverHost.Client, disposeClient: false);
        });

        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
        await gateway.BuildIndexAsync();

        var invokeResult = await gateway.InvokeAsync(new McpGatewayInvokeRequest(
            ToolId: "test-mcp:json_text_search",
            Query: "managedcode"));

        await Assert.That(invokeResult.IsSuccess).IsTrue();
        await Assert.That(invokeResult.Output).IsTypeOf<JsonElement>();

        var output = (JsonElement)invokeResult.Output!;
        await Assert.That(GetJsonProperty(output, "query").GetString()).IsEqualTo("managedcode");
        await Assert.That(GetJsonProperty(output, "source").GetString()).IsEqualTo("text-json");
    }

    [TUnit.Core.Test]
    public async Task InvokeAsync_ReturnsPlainTextWhenMcpTextContentIsNotJson()
    {
        await using var serverHost = await TestMcpServerHost.StartAsync();
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddMcpClient("test-mcp", serverHost.Client, disposeClient: false);
        });

        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
        await gateway.BuildIndexAsync();

        var invokeResult = await gateway.InvokeAsync(new McpGatewayInvokeRequest(
            ToolId: "test-mcp:plain_text_search",
            Query: "managedcode"));

        await Assert.That(invokeResult.IsSuccess).IsTrue();
        await Assert.That(invokeResult.Output).IsTypeOf<string>();
        await Assert.That((string)invokeResult.Output!).IsEqualTo("plain:managedcode");
    }

    private static AIFunction CreateFunction(Delegate callback, string name, string description)
        => AIFunctionFactory.Create(
            callback,
            new AIFunctionFactoryOptions
            {
                Name = name,
                Description = description
            });

    private static string TextUppercase([Description("Text to uppercase.")] string query) => query.ToUpperInvariant();

    private static string OptionalQueryEcho([Description("Text to uppercase.")] string? query = null)
        => (query ?? "missing").ToUpperInvariant();

    private static string EchoContextSummary(
        [Description("Main query text.")] string query,
        [Description("Execution context summary.")] string contextSummary)
        => $"{query}|{contextSummary}";

    private static string ReadStructuredContext([Description("Structured execution context.")] JsonElement context)
        => $"{context.GetProperty("domain").GetString()}|{context.GetProperty("page").GetString()}";

    private static string AlphaSharedSearch([Description("Shared query text.")] string query) => $"alpha:{query}";

    private static string BetaSharedSearch([Description("Shared query text.")] string query) => $"beta:{query}";

    private static JsonElement ReturnJsonString() => JsonSerializer.SerializeToElement("done");

    private static string ThrowingTool() => throw new InvalidOperationException("boom");

    private static JsonElement GetJsonProperty(JsonElement element, string name)
        => element.EnumerateObject()
            .First(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            .Value;
}
