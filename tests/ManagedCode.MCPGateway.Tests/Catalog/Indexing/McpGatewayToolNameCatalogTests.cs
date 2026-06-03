using System.ComponentModel;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayToolNameCatalogTests
{
    [TUnit.Core.Test]
    public async Task ListToolsAsync_KeepsUniqueShortMcpSafeToolNameUnchanged()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddTool(
                "notion",
                TestFunctionFactory.CreateFunction(
                    FetchNotionPage,
                    "notion-fetch",
                    "Fetch a Notion page by id."
                )
            );
        });
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        var descriptor = (await gateway.ListToolsAsync()).Single();

        await Assert.That(descriptor.ToolId).IsEqualTo("notion-fetch");
        await Assert.That(descriptor.ToolName).IsEqualTo("notion-fetch");
        await Assert.That(HasHashSuffix(descriptor.ToolId)).IsFalse();
        await AssertMcpSafeNameAsync(descriptor.ToolId);
    }

    [TUnit.Core.Test]
    public async Task ListToolsAsync_NormalizesSdkSafeDotsAndPreservesRawToolName()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddTool(
                "notion",
                TestFunctionFactory.CreateFunction(
                    FetchNotionPage,
                    "Notion.Fetch.Page",
                    "Fetch a Notion page by id."
                )
            );
        });
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        var descriptor = (await gateway.ListToolsAsync()).Single();
        var invokeResult = await gateway.InvokeAsync(
            new McpGatewayInvokeRequest(
                ToolId: descriptor.ToolId,
                Arguments: new Dictionary<string, object?> { ["pageId"] = "page-7" }
            )
        );

        await Assert.That(descriptor.ToolId).IsEqualTo("notion_fetch_page");
        await Assert.That(descriptor.ToolName).IsEqualTo("Notion.Fetch.Page");
        await Assert.That(invokeResult.IsSuccess).IsTrue();
        await Assert.That(invokeResult.Output).IsEqualTo("page:page-7");
        await AssertMcpSafeNameAsync(descriptor.ToolId);
    }

    [TUnit.Core.Test]
    public async Task ListToolsAsync_HashShortensLongSdkSafeToolName()
    {
        var rawToolName = CreateLongSdkSafeToolName('a');
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddTool(
                "notion",
                TestFunctionFactory.CreateFunction(
                    FetchNotionPage,
                    rawToolName,
                    "Fetch a Notion page by id."
                )
            );
        });
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        var descriptor = (await gateway.ListToolsAsync()).Single();

        await Assert.That(descriptor.ToolName).IsEqualTo(rawToolName);
        await Assert.That(descriptor.ToolId).StartsWith("notion_fetch_page_details");
        await Assert.That(HasHashSuffix(descriptor.ToolId)).IsTrue();
        await AssertMcpSafeNameAsync(descriptor.ToolId);
    }

    [TUnit.Core.Test]
    public async Task InvokeAsync_ResolvesHashShortenedToolIdCaseInsensitively()
    {
        var rawToolName = CreateLongSdkSafeToolName('b');
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddTool(
                "notion",
                TestFunctionFactory.CreateFunction(
                    FetchNotionPage,
                    rawToolName,
                    "Fetch a Notion page by id."
                )
            );
        });
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
        var descriptor = (await gateway.ListToolsAsync()).Single();

        var invokeResult = await gateway.InvokeAsync(
            new McpGatewayInvokeRequest(
                ToolId: descriptor.ToolId.ToUpperInvariant(),
                Arguments: new Dictionary<string, object?> { ["pageId"] = "page-8" }
            )
        );

        await Assert.That(invokeResult.IsSuccess).IsTrue();
        await Assert.That(invokeResult.ToolId).IsEqualTo(descriptor.ToolId);
        await Assert.That(invokeResult.Output).IsEqualTo("page:page-8");
    }

    [TUnit.Core.Test]
    public async Task BuildIndexAsync_SourceQualifiesDuplicateNormalizedNamesAcrossSources()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddTool(
                "docs",
                TestFunctionFactory.CreateFunction(
                    FetchNotionPage,
                    "shared.tool",
                    "Fetch a Notion page by id."
                )
            );
            options.AddTool(
                "ops",
                TestFunctionFactory.CreateFunction(
                    FetchOpsPage,
                    "shared.tool",
                    "Fetch an ops page by id."
                )
            );
        });
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        var tools = await gateway.ListToolsAsync();
        var toolIds = tools.Select(static descriptor => descriptor.ToolId).ToArray();

        await Assert.That(toolIds).IsEquivalentTo(["docs_shared_tool", "ops_shared_tool"]);
        await Assert.That(toolIds.All(IsMcpSafeName)).IsTrue();
    }

    [TUnit.Core.Test]
    public async Task BuildIndexAsync_HashShortensLongDuplicateNormalizedNamesAcrossSources()
    {
        var rawToolName = CreateLongSdkSafeToolName('c');
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddTool(
                "very-long-notion-source-name-alpha",
                TestFunctionFactory.CreateFunction(
                    FetchNotionPage,
                    rawToolName,
                    "Fetch a Notion page by id."
                )
            );
            options.AddTool(
                "very-long-notion-source-name-beta",
                TestFunctionFactory.CreateFunction(
                    FetchOpsPage,
                    rawToolName,
                    "Fetch an ops page by id."
                )
            );
        });
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        var tools = await gateway.ListToolsAsync();
        var toolIds = tools.Select(static descriptor => descriptor.ToolId).ToArray();

        await Assert.That(toolIds.Length).IsEqualTo(2);
        await Assert.That(toolIds.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(2);
        await Assert.That(toolIds.All(IsMcpSafeName)).IsTrue();
        await Assert.That(toolIds.All(HasHashSuffix)).IsTrue();
        await Assert
            .That(tools.All(descriptor => string.Equals(descriptor.ToolName, rawToolName, StringComparison.Ordinal)))
            .IsTrue();
    }

    [TUnit.Core.Test]
    public async Task InvokeAsync_ResolvesRawToolNameAndSourceIdWhenToolIdIsSourceQualified()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.AddTool(
                "docs",
                TestFunctionFactory.CreateFunction(
                    FetchNotionPage,
                    "shared.tool",
                    "Fetch a Notion page by id."
                )
            );
            options.AddTool(
                "ops",
                TestFunctionFactory.CreateFunction(
                    FetchOpsPage,
                    "shared.tool",
                    "Fetch an ops page by id."
                )
            );
        });
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        var invokeResult = await gateway.InvokeAsync(
            new McpGatewayInvokeRequest(
                ToolName: "shared.tool",
                SourceId: "ops",
                Arguments: new Dictionary<string, object?> { ["pageId"] = "page-9" }
            )
        );

        await Assert.That(invokeResult.IsSuccess).IsTrue();
        await Assert.That(invokeResult.ToolId).IsEqualTo("ops_shared_tool");
        await Assert.That(invokeResult.Output).IsEqualTo("ops:page-9");
    }

    private static string CreateLongSdkSafeToolName(char marker) =>
        string.Concat("notion.fetch.page.details.from.customer.workspace.database.", new string(marker, 60));

    private static async Task AssertMcpSafeNameAsync(string name)
    {
        await Assert.That(IsMcpSafeName(name)).IsTrue();
        await Assert.That(name.Length).IsLessThanOrEqualTo(McpGatewayProtocolName.MaxNameLength);
    }

    private static bool IsMcpSafeName(string name) =>
        name.Length is > 0 and <= McpGatewayProtocolName.MaxNameLength
        && name.All(static character =>
            character is >= 'a' and <= 'z'
            || character is >= '0' and <= '9'
            || character is '_' or '-'
        );

    private static bool HasHashSuffix(string name)
    {
        if (name.Length < 9 || name[^9] != '_')
        {
            return false;
        }

        return name[^8..].All(static character =>
            character is >= 'a' and <= 'f' || character is >= '0' and <= '9'
        );
    }

    private static string FetchNotionPage([Description("Page identifier.")] string pageId) =>
        $"page:{pageId}";

    private static string FetchOpsPage([Description("Page identifier.")] string pageId) =>
        $"ops:{pageId}";
}
