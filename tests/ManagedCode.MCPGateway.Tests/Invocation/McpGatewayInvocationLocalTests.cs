using ManagedCode.MCPGateway.Abstractions;

using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Tests;

public sealed partial class McpGatewayInvocationTests
{
    [TUnit.Core.Test]
    public async Task InvokeAsync_InvokesLocalFunctionAndMapsQueryArgument()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddTool(
                "local",
                TestFunctionFactory.CreateFunction(TextUppercase, "text_uppercase", "Convert query text to uppercase."));
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
                TestFunctionFactory.CreateFunction(OptionalQueryEcho, "optional_query_echo", "Echo optional query text in uppercase."));
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
                TestFunctionFactory.CreateFunction(EchoContextSummary, "context_summary_echo", "Echo query and context summary."));
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
                TestFunctionFactory.CreateFunction(ReadStructuredContext, "structured_context_echo", "Read structured context payload."));
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
                TestFunctionFactory.CreateFunction(EchoContextSummary, "context_summary_echo", "Echo query and context summary."));
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
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(ConfigureSharedSearchTools);

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
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(ConfigureSharedSearchTools);

        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
        await gateway.BuildIndexAsync();

        var invokeResult = await gateway.InvokeAsync(new McpGatewayInvokeRequest(
            ToolName: "shared_search",
            Query: "hello"));

        await Assert.That(invokeResult.IsSuccess).IsFalse();
        await Assert.That(invokeResult.Error!.Contains("ambiguous", StringComparison.OrdinalIgnoreCase)).IsTrue();
    }

    private static void ConfigureSharedSearchTools(McpGatewayOptions options)
    {
        options.AddTool("alpha", TestFunctionFactory.CreateFunction(AlphaSharedSearch, "shared_search", "Alpha search tool."));
        options.AddTool("beta", TestFunctionFactory.CreateFunction(BetaSharedSearch, "shared_search", "Beta search tool."));
    }

    [TUnit.Core.Test]
    public async Task InvokeAsync_ReturnsNotFoundWhenToolDoesNotExist()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddTool("local", TestFunctionFactory.CreateFunction(TextUppercase, "text_uppercase", "Convert query text to uppercase."));
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
            options.AddTool("local", TestFunctionFactory.CreateFunction(ReturnJsonString, "json_string_result", "Return a JSON string scalar."));
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
            options.AddTool("local", TestFunctionFactory.CreateFunction(ThrowingTool, "throwing_tool", "Throw an exception for test coverage."));
        });

        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
        await gateway.BuildIndexAsync();

        var invokeResult = await gateway.InvokeAsync(new McpGatewayInvokeRequest(
            ToolId: "local:throwing_tool"));

        await Assert.That(invokeResult.IsSuccess).IsFalse();
        await Assert.That(invokeResult.Error).IsEqualTo("boom");
    }
}
