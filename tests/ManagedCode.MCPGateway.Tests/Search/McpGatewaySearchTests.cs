using System.ComponentModel;

using Microsoft.Extensions.AI;

namespace ManagedCode.MCPGateway.Tests;

public sealed partial class McpGatewaySearchTests
{
    private static void ConfigureSearchTools(McpGatewayOptions options)
    {
        options.AddTool("local", TestFunctionFactory.CreateFunction(SearchGitHub, "github_search_issues", "Search GitHub issues and pull requests by user query."));
        options.AddTool("local", TestFunctionFactory.CreateFunction(SearchWeather, "weather_search_forecast", "Search weather forecast and temperature information by city name."));
    }

    private static string SearchGitHub([Description("Search query text.")] string query) => $"github:{query}";

    private static string SearchGitHubAgain([Description("Search query text.")] string query) => $"github-duplicate:{query}";

    private static string SearchWeather([Description("City or weather request text.")] string query) => $"weather:{query}";

    private static string FilterAdvisories([Description("Severity filter to apply to advisory lookups.")] string severity)
        => $"advisory:{severity}";
}

internal sealed class ScopedEmbeddingGeneratorTracker
{
    public List<Guid> InstanceIds { get; } = [];

    public List<IReadOnlyList<string>> Calls { get; } = [];
}

internal sealed class ScopedTestEmbeddingGenerator(ScopedEmbeddingGeneratorTracker tracker)
    : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly Guid _instanceId = Guid.NewGuid();
    private readonly TestEmbeddingGenerator _inner = new();

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        tracker.InstanceIds.Add(_instanceId);
        return GenerateAndCaptureAsync(values, options, cancellationToken);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => _inner.GetService(serviceType, serviceKey);

    public void Dispose() => _inner.Dispose();

    private async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAndCaptureAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options,
        CancellationToken cancellationToken)
    {
        var result = await _inner.GenerateAsync(values, options, cancellationToken);
        tracker.Calls.Add(_inner.Calls[^1]);
        return result;
    }
}
