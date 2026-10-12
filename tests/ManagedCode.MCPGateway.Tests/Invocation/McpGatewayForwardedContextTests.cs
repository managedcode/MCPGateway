using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayForwardedContextTests
{
    [Test]
    public async Task Restricted_context_forwards_only_explicit_business_keys_and_no_summary_or_forged_identity()
    {
        await using var upstream = await TestMcpServerHost.StartAsync();
        await using var provider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.Execution.RestrictForwardedContext = true;
            options.Execution.ForwardedContextKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "page" };
            options.AddMcpClient("upstream", upstream.Client);
        });
        var result = await provider.GetRequiredService<IMcpGateway>().InvokeAsync(new(ToolName: "github_repository_search", Query: "weather",
            ContextSummary: "SEEDED_SECRET", Context: new Dictionary<string, object?> { ["page"] = "settings", ["tenant"] = "forged", ["secret"] = "SEEDED_SECRET" }));
        await Assert.That(result.IsSuccess).IsTrue();
        var meta = upstream.CapturedMeta.Single()["managedCodeMcpGateway"]!;
        await Assert.That(meta.ToJsonString()).DoesNotContain("SEEDED_SECRET");
        await Assert.That(meta.ToJsonString()).DoesNotContain("forged");
        await Assert.That(meta["context"]!["page"]!.GetValue<string>()).IsEqualTo("settings");
    }
}
