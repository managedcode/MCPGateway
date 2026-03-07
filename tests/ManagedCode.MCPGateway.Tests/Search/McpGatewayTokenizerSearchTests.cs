using System.ComponentModel;

using ManagedCode.MCPGateway.Abstractions;

using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayTokenizerSearchTests
{
    [TUnit.Core.Test]
    public async Task BuildIndexAsync_SkipsEmbeddingsWhenTokenizerStrategyIsSelected()
    {
        var embeddingGenerator = new TestEmbeddingGenerator();
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(
            ConfigureTokenizerSearchTools,
            embeddingGenerator);
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        var buildResult = await gateway.BuildIndexAsync();
        var searchResult = await gateway.SearchAsync("github pull requests", maxResults: 1);

        await Assert.That(buildResult.VectorizedToolCount).IsEqualTo(0);
        await Assert.That(buildResult.IsVectorSearchEnabled).IsFalse();
        await Assert.That(embeddingGenerator.Calls.Count).IsEqualTo(0);
        await Assert.That(searchResult.RankingMode).IsEqualTo("lexical");
        await Assert.That(searchResult.Diagnostics.Any(static diagnostic => diagnostic.Code == "lexical_fallback")).IsFalse();
        await Assert.That(searchResult.Matches[0].ToolId).IsEqualTo("local:github_search_issues");
    }

    [TUnit.Core.Test]
    public async Task SearchAsync_UsesTheBuiltInChatGptTokenizerProfile()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            TokenizerSearchTestSupport.UseTokenizerSearch(options);
            options.AddTool(
                "local",
                TestFunctionFactory.CreateFunction(
                    SearchGitHubPullRequests,
                    "github_pull_request_search",
                    "Search GitHub pull requests by repository, reviewer, branch, or merge status."));
            options.AddTool(
                "local",
                TestFunctionFactory.CreateFunction(
                    FilterAdvisories,
                    "advisory_lookup",
                    "Lookup advisory records by severity, ecosystem, package, or CVE reference."));
        });
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        await gateway.BuildIndexAsync();
        var advisorySearch = await gateway.SearchAsync("critical severity advisory", maxResults: 1);
        var pullRequestSearch = await gateway.SearchAsync("review queue for pull requests", maxResults: 1);

        await Assert.That(advisorySearch.RankingMode).IsEqualTo("lexical");
        await Assert.That(advisorySearch.Matches[0].ToolId).IsEqualTo("local:advisory_lookup");
        await Assert.That(pullRequestSearch.Matches[0].ToolId).IsEqualTo("local:github_pull_request_search");
    }

    [TUnit.Core.Test]
    public async Task SearchAsync_PrefersFilesystemLookupOverFinanceStatusForInvoicePdfQueries()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            TokenizerSearchTestSupport.UseTokenizerSearch(options);
            options.AddTool(
                "local",
                TestFunctionFactory.CreateFunction(
                    SearchWeatherForecast,
                    "filesystem_find_files",
                    "Find or locate files, PDFs, report documents, and exported invoice files by folder, reports workspace, glob pattern, extension, or text content. Not for invoice payment status."));
            options.AddTool(
                "local",
                TestFunctionFactory.CreateFunction(
                    FilterAdvisories,
                    "finance_invoice_search",
                    "Find invoices, bills, and billing records by customer, invoice number, payment state, or due date."));
        });
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        await gateway.BuildIndexAsync();
        var searchResult = await gateway.SearchAsync("where did the invoice pdf go in reports", maxResults: 1);

        await Assert.That(searchResult.Matches[0].ToolId).IsEqualTo("local:filesystem_find_files");
    }

    [TUnit.Core.Test]
    public async Task SearchAsync_PrefersInventoryLookupOverCatalogSearchForWarehouseAvailability()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            TokenizerSearchTestSupport.UseTokenizerSearch(options);
            options.AddTool(
                "local",
                TestFunctionFactory.CreateFunction(
                    SearchWeatherForecast,
                    "commerce_catalog_search",
                    "Search product catalog by keyword, category, brand, or attribute filters."));
            options.AddTool(
                "local",
                TestFunctionFactory.CreateFunction(
                    SearchWeatherForecast,
                    "commerce_inventory_lookup",
                    "Lookup inventory, stock by SKU, warehouse balance, and availability."));
        });
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        await gateway.BuildIndexAsync();
        var searchResult = await gateway.SearchAsync("is sku keyboard-ergo available in warehouse", maxResults: 1);

        await Assert.That(searchResult.Matches[0].ToolId).IsEqualTo("local:commerce_inventory_lookup");
    }

    private static void ConfigureTokenizerSearchTools(McpGatewayOptions options)
    {
        TokenizerSearchTestSupport.UseTokenizerSearch(options);
        options.AddTool(
            "local",
            TestFunctionFactory.CreateFunction(
                SearchGitHubPullRequests,
                "github_search_issues",
                "Search GitHub issues and pull requests by user query."));
        options.AddTool(
            "local",
            TestFunctionFactory.CreateFunction(
                SearchWeatherForecast,
                "weather_search_forecast",
                "Search weather forecast and temperature information by city name."));
    }

    private static string SearchGitHubPullRequests(
        [Description("Repository owner or organization handle.")] string owner,
        [Description("Repository name or slug.")] string repository,
        [Description("Free-form pull request search terms or reviewer names.")] string query,
        [Description("Workflow state such as open, closed, or merged.")] WorkItemState state)
        => $"{owner}/{repository}:{query}:{state}";

    private static string SearchWeatherForecast(
        [Description("City, airport code, or geo place name.")] string location,
        [Description("Forecast window such as today, weekend, or next 5 days.")] string timeRange,
        [Description("Preferred temperature unit.")] TemperatureUnit unit,
        [Description("Optional weather focus such as rain, wind, or air quality.")] string focus)
        => $"{location}:{timeRange}:{unit}:{focus}";

    private static string FilterAdvisories(
        [Description("Severity level to filter advisories.")] TicketSeverity severity,
        [Description("Software ecosystem such as npm, nuget, or container image.")] string ecosystem,
        [Description("Package name, image, or CVE reference to inspect.")] string packageOrReference)
        => $"{severity}:{ecosystem}:{packageOrReference}";

}
