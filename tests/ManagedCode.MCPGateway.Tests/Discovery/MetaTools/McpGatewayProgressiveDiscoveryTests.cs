using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayProgressiveDiscoveryTests
{
    [TUnit.Core.Test]
    public async Task Schema_faithful_proxy_preserves_nested_constraints_and_invokes_real_arguments()
    {
        var function = new OrderFunction();
        await using var provider = GatewayTestServiceProviderFactory.Create(options => options.AddTool("orders", function, new McpGatewayToolSearchHints
        { ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false }));
        var discovery = provider.GetRequiredService<IMcpGatewayDiscovery>();
        var schema = await discovery.GetToolSchemaAsync("ORDER_SUBMIT");
        var proxy = (AIFunction)(await provider.GetRequiredService<McpGatewayDiscoveryToolSet>()
            .CreateSchemaFaithfulToolsAsync(["ORDER_SUBMIT"])).Single();
        await Assert.That(proxy.JsonSchema.GetRawText()).IsEqualTo(function.JsonSchema.GetRawText());
        await Assert.That(proxy.Name).IsEqualTo(schema!.Tool.ToolId);
        await Assert.That(proxy.Description).IsEqualTo(function.Description);
        await Assert.That(ReferenceEquals(proxy.JsonSerializerOptions, McpGatewayJsonSerializer.Options)).IsTrue();
        await Assert.That(proxy.ReturnJsonSchema!.Value.GetProperty("properties").TryGetProperty("outcome", out _)).IsTrue();
        await Assert.That((bool)proxy.AdditionalProperties["ReadOnly"]!).IsTrue();
        await Assert.That((bool)proxy.AdditionalProperties["Idempotent"]!).IsTrue();
        await Assert.That((bool)proxy.AdditionalProperties["Destructive"]!).IsFalse();
        await Assert.That((bool)proxy.AdditionalProperties["OpenWorld"]!).IsFalse();
        await Assert.That(schema!.SchemaHash.Length).IsEqualTo(64);
        await Assert.That(proxy.JsonSchema.GetProperty("properties").GetProperty("order")
            .GetProperty("properties").GetProperty("items").GetProperty("items").GetProperty("required")[0].GetString()).IsEqualTo("sku");
        var result = (JsonElement)(await proxy.InvokeAsync(new AIFunctionArguments
        {
            ["order"] = new { items = new[] { new { sku = "A123" } }, priority = "normal" },
        }))!;
        await Assert.That(result.GetProperty("isSuccess").GetBoolean()).IsTrue();
        await Assert.That(result.GetProperty("output").GetProperty("order").GetProperty("items")[0].GetProperty("sku").GetString()).IsEqualTo("A123");
        await Assert.That(function.LastArguments!.ContainsKey("arguments")).IsFalse();
    }

    [TUnit.Core.Test]
    public async Task Five_thousand_tool_catalog_obeys_serialized_budget_and_continuation_preserves_selected_results()
    {
        await using var provider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.SearchStrategy = McpGatewaySearchStrategy.Embeddings;
            options.MaxSearchResults = 100;
            for (var index = 0; index < 5000; index++)
            {
                options.AddTool("directory", AIFunctionFactory.Create(static () => "value", new AIFunctionFactoryOptions
                { Name = $"directory_search_{index:D4}", Description = "Search contact directory " + new string('x', 200) }));
            }
        }, embeddingGenerator: new TestEmbeddingGenerator());
        var discovery = provider.GetRequiredService<IMcpGatewayDiscovery>();
        var full = await discovery.DiscoverAsync(new("contact directory") { MaxResults = 50, MaxBytes = 100_000 });
        var seen = new List<string>();
        string? continuation = null;
        do
        {
            var page = await discovery.DiscoverAsync(new("contact directory")
            { MaxResults = 50, MaxBytes = 2048, ContinuationToken = continuation });
            var json = JsonSerializer.Serialize(page, McpGatewayJsonSerializer.Options);
            await Assert.That(Encoding.UTF8.GetByteCount(json)).IsEqualTo(page.SerializedBytes);
            await Assert.That(page.SerializedBytes <= 2048).IsTrue();
            await Assert.That(json.Contains("inputSchema", StringComparison.Ordinal)).IsFalse();
            seen.AddRange(page.Tools.Select(tool => tool.ToolId));
            continuation = page.ContinuationToken;
        } while (continuation is not null);
        await Assert.That(string.Join(',', seen)).IsEqualTo(string.Join(',', full.Tools.Select(tool => tool.ToolId)));
        await Assert.That(seen.Count).IsEqualTo(50);
    }

    [TUnit.Core.Test]
    public async Task Discovery_only_SDK_endpoint_lists_three_tools_and_runs_schema_then_invocation()
    {
        await using var host = await GatewayMcpServerHost.StartAsync(options => options.AddTool("orders", new OrderFunction()),
            services => services.Configure<McpGatewayMcpServerExportOptions>(options => options.Mode = McpGatewayMcpServerExportMode.DiscoveryOnly));
        var tools = await host.Client.ListToolsAsync();
        await Assert.That(tools.Count).IsEqualTo(3);
        await Assert.That(tools.Any(tool => tool.Name == "order_submit")).IsFalse();
        var hiddenCall = await host.Client.CallToolAsync("order_submit", new Dictionary<string, object?>());
        await Assert.That(hiddenCall.IsError).IsTrue();
        var schema = await host.Client.CallToolAsync(McpGatewayDiscoveryToolSet.SchemaToolName,
            new Dictionary<string, object?> { ["toolId"] = "order_submit" });
        await Assert.That(((TextContentBlock)schema.Content[0]).Text).Contains("additionalProperties");
        var invoked = await host.Client.CallToolAsync(McpGatewayToolSet.DefaultInvokeToolName,
            new Dictionary<string, object?> { ["toolId"] = "order_submit", ["arguments"] = new { order = new { items = new[] { new { sku = "A123" } }, priority = "normal" } } });
        await Assert.That(((TextContentBlock)invoked.Content[0]).Text).Contains("A123");
    }

    private sealed class OrderFunction : AIFunction
    {
        public override string Name => "order_submit";
        public override string Description => "Submit an order.";
        public override JsonElement JsonSchema { get; } = JsonDocument.Parse("""
            {"type":"object","required":["order"],"additionalProperties":false,"properties":{"order":{"type":"object","required":["items","priority"],"additionalProperties":false,"properties":{"items":{"type":"array","minItems":1,"items":{"type":"object","required":["sku"],"additionalProperties":false,"properties":{"sku":{"type":"string"}}}},"priority":{"type":"string","enum":["normal","urgent"]}}}}}
            """).RootElement.Clone();
        public AIFunctionArguments? LastArguments { get; private set; }
        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            LastArguments = arguments;
            return ValueTask.FromResult<object?>(McpGatewayJsonSerializer.TrySerializeToElement(arguments));
        }
    }
}
