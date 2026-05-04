using System.Globalization;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Benchmarks;

internal static class BenchmarkCatalog
{
    public const int ToolCount = 120;
    public const string WeatherQuery = "umbrella planning for region seven";
    public const string PortfolioQuery = "brokerage holdings snapshot for acme";
    public const string ArchiveQuery = "genealogy record archive lookup";

    public static ServiceProvider CreateGraphServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMcpGateway(options =>
        {
            options.SearchStrategy = McpGatewaySearchStrategy.Graph;
            ConfigureCatalog(options);
        });

        return services.BuildServiceProvider();
    }

    public static async Task<BenchmarkGatewayHost> CreateBuiltGraphGatewayAsync()
    {
        var serviceProvider = CreateGraphServiceProvider();
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
        await gateway.BuildIndexAsync();
        var toolSet = serviceProvider.GetRequiredService<McpGatewayToolSet>();
        return new BenchmarkGatewayHost(serviceProvider, gateway, toolSet);
    }

    public static void ConfigureCatalog(McpGatewayOptions options)
    {
        for (var index = 1; index <= ToolCount; index++)
        {
            var toolIndex = index.ToString("D3", CultureInfo.InvariantCulture);
            var name = $"archive_lookup_{toolIndex}";
            var description = $"Handle archive lookup workflow number {toolIndex} for genealogy records.";

            if (index == 81)
            {
                name = "weather_dispatch_specialist";
                description =
                    "Get weather forecast, rain, wind, umbrella planning, and precipitation details for a city or region.";
            }
            else if (index == 82)
            {
                name = "portfolio_status_specialist";
                description =
                    "Summarize brokerage holdings, market value, exposure, investment risk, and account snapshots.";
            }

            options.AddTool("local", CreateTool(name, description));
        }
    }

    private static AITool CreateTool(string name, string description) =>
        AIFunctionFactory.Create(
            static (string query) => $"benchmark:{query}",
            new AIFunctionFactoryOptions { Name = name, Description = description }
        );
}
