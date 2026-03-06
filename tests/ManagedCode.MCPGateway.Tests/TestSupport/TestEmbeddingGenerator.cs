using Microsoft.Extensions.AI;

namespace ManagedCode.MCPGateway.Tests;

internal sealed class TestEmbeddingGenerator(TestEmbeddingGeneratorOptions? options = null)
    : IEmbeddingGenerator<string, Embedding<float>>
{
    private static readonly string[] Vocabulary =
    [
        "github",
        "issue",
        "issues",
        "pull",
        "request",
        "requests",
        "weather",
        "forecast",
        "temperature",
        "search",
        "contact",
        "directory",
        "context",
        "summary",
        "family",
        "tree",
        "genealogy",
        "page",
        "profile",
        "repository",
        "repositories"
    ];

    private readonly TestEmbeddingGeneratorOptions _options = options ?? new();
    private readonly EmbeddingGeneratorMetadata _metadata =
        new("ManagedCode.MCPGateway.Tests", new Uri("https://example.test"), "test-embedding", Vocabulary.Length);

    public List<IReadOnlyList<string>> Calls { get; } = [];

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var inputList = values.ToList();
        Calls.Add(inputList);

        if (_options.ThrowOnInput is not null &&
            inputList.Any(_options.ThrowOnInput))
        {
            throw new InvalidOperationException("Embedding generation failed for a test input.");
        }

        var embeddings = inputList
            .Select(CreateEmbedding)
            .ToList();

        if (_options.ReturnMismatchedBatchCount &&
            inputList.Count > 1 &&
            embeddings.Count > 0)
        {
            embeddings.RemoveAt(embeddings.Count - 1);
        }

        GeneratedEmbeddings<Embedding<float>> result =
        [
            .. embeddings
        ];

        return Task.FromResult(result);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType == typeof(EmbeddingGeneratorMetadata))
        {
            return _metadata;
        }

        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
    }

    private Embedding<float> CreateEmbedding(string value)
    {
        if (_options.ReturnZeroVectorOnInput?.Invoke(value) == true)
        {
            return new Embedding<float>(new float[Vocabulary.Length]);
        }

        var vector = new float[Vocabulary.Length];
        var normalized = value.ToLowerInvariant();

        for (var index = 0; index < Vocabulary.Length; index++)
        {
            if (normalized.Contains(Vocabulary[index], StringComparison.Ordinal))
            {
                vector[index] = 1f;
            }
        }

        return new Embedding<float>(vector);
    }
}

internal sealed class TestEmbeddingGeneratorOptions
{
    public Func<string, bool>? ThrowOnInput { get; init; }

    public Func<string, bool>? ReturnZeroVectorOnInput { get; init; }

    public bool ReturnMismatchedBatchCount { get; init; }
}
