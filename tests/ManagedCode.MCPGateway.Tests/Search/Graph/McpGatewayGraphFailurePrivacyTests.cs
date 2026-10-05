using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayGraphFailurePrivacyTests
{
    private const string SourceId = "local";
    private const string ToolName = "graph_privacy_probe";
    private const string ToolDescription = "Search graph privacy probe.";
    private const string QueryMarker = "graph-query-marker-314159";
    private const string EndpointMarker = "endpoint-marker";
    private const string FederatedEndpoint = "http://127.0.0.1:1/endpoint-marker";
    private const string ExpectedFailure = "Markdown-LD graph ranking failed.";
    private const string GraphFailureCode = "graph_search_failed";

    [TUnit.Core.Test]
    public async Task SearchGraphAsync_UsesSafeDiagnosticAndLoggerStateOnProviderFailure()
    {
        using var logs = new GatewayCapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.AddProvider(logs);
            logging.SetMinimumLevel(LogLevel.Trace);
        });
        services.AddMcpGateway(options =>
        {
            options.AddMarkdownLdFederatedServiceEndpoint(new Uri(FederatedEndpoint));
            options.AddTool(
                SourceId,
                TestFunctionFactory.CreateFunction(EchoQuery, ToolName, ToolDescription)
            );
        });
        await using var serviceProvider = services.BuildServiceProvider();
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
        var graphSearch = serviceProvider.GetRequiredService<IMcpGatewayGraphSearch>();
        await gateway.BuildIndexAsync();

        var result = await graphSearch.SearchGraphAsync(
            new McpGatewayGraphSearchRequest(QueryMarker)
            {
                UseFederation = true,
                IncludeLocalGatewayGraph = false,
                ServiceEndpoints = [FederatedEndpoint],
            }
        );

        var failure = result.Diagnostics.Single(diagnostic => diagnostic.Code == GraphFailureCode);
        await Assert.That(failure.Message).IsEqualTo(ExpectedFailure);
        await AssertNoSensitiveMarkersAsync(failure.Message);
        await AssertNoSensitiveMarkersAsync(
            string.Join(Environment.NewLine, logs.Entries.Select(FlattenLog))
        );
        await Assert.That(logs.Entries.All(static entry => entry.ExceptionText is null)).IsTrue();
    }

    private static string EchoQuery(string query) => query;

    private static string FlattenLog(GatewayCapturedLog entry) =>
        string.Join(Environment.NewLine, entry.Category, entry.Message, entry.ExceptionText);

    private static async Task AssertNoSensitiveMarkersAsync(string value)
    {
        await Assert.That(value.Contains(QueryMarker, StringComparison.Ordinal)).IsFalse();
        await Assert.That(value.Contains(EndpointMarker, StringComparison.Ordinal)).IsFalse();
    }
}
