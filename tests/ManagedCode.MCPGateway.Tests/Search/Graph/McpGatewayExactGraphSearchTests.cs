using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayExactGraphSearchTests
{
    [Test]
    [Arguments(8, "create")]
    [Arguments(40, "create")]
    [Arguments(8, "read")]
    [Arguments(40, "read")]
    public async Task Exact_tool_identity_survives_common_prefixes_and_action_word_normalization(int siblingCount, string action)
    {
        var targetName = $"catalog_files_{action}";
        await using var provider = GatewayTestServiceProviderFactory.Create(options =>
        {
            for (var index = 0; index < siblingCount; index++)
            {
                options.AddTool("local", TestFunctionFactory.CreateFunction(
                    (string path) => path,
                    $"catalog_files_inspect_{index}",
                    "Inspect a catalog file; create it first with catalog_files_create."));
            }
            options.AddTool("local", TestFunctionFactory.CreateFunction(
                (string path) => path, targetName, "Create a new catalog file at the supplied path."));
        });
        var toolSet = new McpGatewayToolSet(provider.GetRequiredService<IMcpGateway>(),
            provider.GetRequiredService<IMcpGatewayGraphSearch>());
        var search = await provider.GetRequiredService<IMcpGateway>().SearchAsync(targetName, maxResults: 1);
        await Assert.That(search.Matches).HasSingleItem();
        await Assert.That(search.Matches[0].ToolId).IsEqualTo(targetName);
        var result = await toolSet.SchemaGraphSearchAsync(targetName, maxResults: 1);
        await Assert.That(result.Matches).HasSingleItem();
        await Assert.That(result.Matches[0].ToolMatch?.ToolId).IsEqualTo(targetName);
        await Assert.That(result.GeneratedSparql).Contains("SELECT");
        await Assert.That(result.Matches[0].Evidence.Count).IsGreaterThan(0);
        var invoked = await toolSet.InvokeAsync(result.Matches[0].ToolMatch!.ToolId,
            new Dictionary<string, object?> { ["path"] = "created.txt" });
        await Assert.That(invoked.IsSuccess).IsTrue();
        await Assert.That(invoked.Output).IsEqualTo("created.txt");
    }
}
