using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Tests;

public sealed partial class McpGatewaySearchTests
{
    [TUnit.Core.Test]
    public async Task JsonLd_file_roundtrip_preserves_graph_and_search()
    {
        var graphFile = Path.ChangeExtension(CreateTemporaryGraphFilePath(), ".jsonld");
        try
        {
            await using var authoring = GatewayTestServiceProviderFactory.Create(options =>
            {
                ConfigureSearchTools(options);
                options.UseMarkdownLdGraphDocuments(descriptors => McpGatewayMarkdownLdGraphFile
                    .CreateDocuments(descriptors)
                    .Select(document => document with { Content = document.Content + "\n\nFile-owned aurora observatory metadata." })
                    .ToArray());
            });
            var exported = await authoring.GetRequiredService<IMcpGatewayGraphSearch>().ExportMarkdownLdGraphAsync();
            Directory.CreateDirectory(Path.GetDirectoryName(graphFile)!);
            await File.WriteAllTextAsync(graphFile, exported.JsonLd);
            await using var provider = GatewayTestServiceProviderFactory.Create(options =>
            {
                ConfigureSearchTools(options);
                options.UseMarkdownLdGraphFile(graphFile);
            });
            var gateway = provider.GetRequiredService<IMcpGateway>();
            var built = await gateway.BuildIndexAsync();
            var search = await gateway.SearchAsync("temperature forecast by city", maxResults: 1);
            var roundtrip = await provider.GetRequiredService<IMcpGatewayGraphSearch>().ExportMarkdownLdGraphAsync();
            await Assert.That(built.IsGraphSearchEnabled).IsTrue();
            await Assert.That(roundtrip.NodeCount).IsEqualTo(exported.NodeCount);
            await Assert.That(roundtrip.EdgeCount).IsEqualTo(exported.EdgeCount);
            await Assert.That(roundtrip.JsonLd).Contains("File-owned aurora observatory metadata.");
            await Assert.That(search.RankingMode).IsEqualTo("graph");
            await Assert.That(search.Matches[0].ToolId).IsEqualTo("weather_search_forecast");
        }
        finally
        {
            File.Delete(graphFile);
        }
    }

    [TUnit.Core.Test]
    [TUnit.Core.Arguments("[]")]
    [TUnit.Core.Arguments("invalid json")]
    [TUnit.Core.Arguments(null)]
    public async Task JsonLd_file_missing_invalid_or_incomplete_does_not_regenerate(string? content)
    {
        var graphFile = Path.ChangeExtension(CreateTemporaryGraphFilePath(), ".jsonld");
        try
        {
            if (content is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(graphFile)!);
                await File.WriteAllTextAsync(graphFile, content);
            }

            await using var provider = GatewayTestServiceProviderFactory.Create(options =>
            {
                ConfigureSearchTools(options);
                options.UseMarkdownLdGraphFile(graphFile);
            });
            var built = await provider.GetRequiredService<IMcpGateway>().BuildIndexAsync();
            await Assert.That(built.IsGraphSearchEnabled).IsFalse();
            await Assert.That(built.GraphNodeCount).IsEqualTo(0);
            await Assert.That(built.Diagnostics.Count).IsGreaterThan(0);
        }
        finally
        {
            File.Delete(graphFile);
        }
    }
}
