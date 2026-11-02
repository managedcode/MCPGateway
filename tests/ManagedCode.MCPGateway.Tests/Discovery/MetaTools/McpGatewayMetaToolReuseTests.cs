using System.Text.Json;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayMetaToolReuseTests
{
    [TUnit.Core.Test]
    [TUnit.Core.Arguments(false)]
    [TUnit.Core.Arguments(true)]
    public async Task Default_tool_lists_reject_mutation_and_keep_real_invocation_working(bool graphTools)
    {
        await using var provider = CreateProvider("owner");
        var gateway = provider.GetRequiredService<IMcpGateway>();
        await gateway.BuildIndexAsync();
        var toolSet = provider.GetRequiredService<McpGatewayToolSet>();
        var tools = graphTools ? toolSet.CreateGraphTools() : toolSet.CreateTools();

        Assert.Throws<NotSupportedException>(() => ((IList<AITool>)tools)[0] = tools[0]);
        var invoke = toolSet.CreateTools().OfType<AIFunction>().Single(tool => tool.Name == McpGatewayToolSet.DefaultInvokeToolName);
        var result = await InvokeEchoAsync(invoke);

        await Assert.That(result.GetProperty("isSuccess").GetBoolean()).IsTrue();
        await Assert.That(result.GetProperty("output").GetString()).IsEqualTo("owner:evidence");
        var aliases = toolSet.CreateTools("custom_search", "custom_route", "custom_invoke");
        await Assert.That(aliases.Select(tool => tool.Name)).IsEquivalentTo(["custom_search", "custom_route", "custom_invoke"]);
        await Assert.That(toolSet.CreateTools().Select(tool => tool.Name)).Contains(McpGatewayToolSet.DefaultInvokeToolName);
    }

    [TUnit.Core.Test]
    public async Task Repeated_default_meta_tools_stay_bound_to_their_own_gateway()
    {
        await using var first = CreateProvider("first");
        await using var second = CreateProvider("second");
        var firstGateway = first.GetRequiredService<IMcpGateway>();
        var secondGateway = second.GetRequiredService<IMcpGateway>();
        await firstGateway.BuildIndexAsync();
        await secondGateway.BuildIndexAsync();
        for (var iteration = 0; iteration < 3; iteration++)
        {
            var firstTool = firstGateway.CreateMetaTools().OfType<AIFunction>().Single(tool => tool.Name == McpGatewayToolSet.DefaultInvokeToolName);
            var secondTool = secondGateway.CreateMetaTools().OfType<AIFunction>().Single(tool => tool.Name == McpGatewayToolSet.DefaultInvokeToolName);
            await Assert.That((await InvokeEchoAsync(firstTool)).GetProperty("output").GetString()).IsEqualTo("first:evidence");
            await Assert.That((await InvokeEchoAsync(secondTool)).GetProperty("output").GetString()).IsEqualTo("second:evidence");
        }
    }

    [TUnit.Core.Test]
    public async Task Previously_created_meta_tools_use_the_reconfigured_catalog()
    {
        await using var provider = CreateProvider("before");
        var gateway = provider.GetRequiredService<IMcpGateway>();
        var invoke = gateway.CreateMetaTools().OfType<AIFunction>().Single(tool => tool.Name == McpGatewayToolSet.DefaultInvokeToolName);
        var toolSet = provider.GetRequiredService<McpGatewayToolSet>();
        var build = toolSet.CreateGraphTools().OfType<AIFunction>().Single(tool => tool.Name == McpGatewayToolSet.DefaultToolIndexBuildToolName);
        await Assert.That(((JsonElement)(await build.InvokeAsync(new AIFunctionArguments()))!).GetProperty("toolCount").GetInt32()).IsEqualTo(1);
        await Assert.That((await InvokeEchoAsync(invoke)).GetProperty("output").GetString()).IsEqualTo("before:evidence");

        var replacement = new McpGatewayOptions();
        replacement.AddTool("local", TestFunctionFactory.CreateFunction((string value) => "after:" + value, "cached_echo", "Echo updated evidence."));
        replacement.AddTool("local", TestFunctionFactory.CreateFunction(() => "second", "second_echo", "Second evidence tool."));
        await provider.GetRequiredService<IMcpGatewayCatalogRuntime>().ReconfigureAsync(replacement);

        await Assert.That(((JsonElement)(await build.InvokeAsync(new AIFunctionArguments()))!).GetProperty("toolCount").GetInt32()).IsEqualTo(2);
        await Assert.That((await InvokeEchoAsync(invoke)).GetProperty("output").GetString()).IsEqualTo("after:evidence");
        var aliases = toolSet.CreateGraphTools("custom_graph", "custom_federation", "custom_export", "custom_schema", "custom_build");
        await Assert.That(aliases.Select(tool => tool.Name)).IsEquivalentTo(["custom_graph", "custom_federation", "custom_export", "custom_schema", "custom_build"]);
    }

    private static ServiceProvider CreateProvider(string owner) => GatewayTestServiceProviderFactory.Create(options =>
        options.AddTool("local", TestFunctionFactory.CreateFunction((string value) => owner + ":" + value, "cached_echo", "Echo caller evidence.")));

    private static async Task<JsonElement> InvokeEchoAsync(AIFunction function)
    {
        var result = await function.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["toolId"] = "cached_echo",
            ["arguments"] = new Dictionary<string, object?> { ["value"] = "evidence" },
        }));
        return (JsonElement)result!;
    }
}
