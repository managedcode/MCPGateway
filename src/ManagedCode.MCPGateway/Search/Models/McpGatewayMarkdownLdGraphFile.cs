using System.Text.Json;
using ManagedCode.MarkdownLd.Kb.Pipeline;

namespace ManagedCode.MCPGateway;

public sealed record McpGatewayMarkdownLdGraphDocument(
    string Path,
    string Content,
    string? CanonicalUri = null
);

public sealed record McpGatewayMarkdownLdGraphExport(
    string JsonLd,
    string Turtle,
    string MermaidFlowchart,
    string DotGraph,
    int NodeCount,
    int EdgeCount
);

public static class McpGatewayMarkdownLdGraphFile
{
    public const int CurrentVersion = 1;

    private const string EmptyDocumentPathMessage = "Markdown-LD graph document path is required.";
    private const string EmptyDocumentContentMessage =
        "Markdown-LD graph document content is required.";
    private const string InvalidGraphFileMessage = "Markdown-LD graph file is invalid.";
    private const string InvalidDocumentCanonicalUriMessage =
        "Markdown-LD graph document canonical URI is invalid.";
    private const string UnsupportedGraphFileVersionMessage =
        "Markdown-LD graph file version is not supported.";

    public static IReadOnlyList<McpGatewayMarkdownLdGraphDocument> CreateDocuments(
        IEnumerable<McpGatewayToolDescriptor> descriptors,
        int maxDescriptorLength = McpGatewayOptions.DefaultMaxDescriptorLength
    )
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        return McpGatewayRuntime.CreateMarkdownLdGraphFileDocuments(
            descriptors.ToArray(),
            Math.Max(McpGatewayOptions.MinimumDescriptorLength, maxDescriptorLength)
        );
    }

    public static Task<McpGatewayMarkdownLdGraphExport> ExportAsync(
        IEnumerable<McpGatewayToolDescriptor> descriptors,
        int maxDescriptorLength = McpGatewayOptions.DefaultMaxDescriptorLength,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        return ExportAsync(CreateDocuments(descriptors, maxDescriptorLength), cancellationToken);
    }

    public static Task<McpGatewayMarkdownLdGraphExport> ExportAsync(
        IEnumerable<McpGatewayMarkdownLdGraphDocument> documents,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(documents);
        return McpGatewayRuntime.ExportMarkdownLdGraphAsync(documents.ToArray(), cancellationToken);
    }

    public static async Task WriteAsync(
        string filePath,
        IEnumerable<McpGatewayMarkdownLdGraphDocument> documents,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(documents);

        var normalizedDocuments = ValidateDocuments(documents.ToArray());
        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var bundle = new Bundle(CurrentVersion, normalizedDocuments);
        var json = JsonSerializer.Serialize(bundle, McpGatewayJsonSerializer.Options);
        await File.WriteAllTextAsync(filePath, json, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<IReadOnlyList<McpGatewayMarkdownLdGraphDocument>> ReadAsync(
        string filePath,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var json = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        return Deserialize(json);
    }

    internal static IReadOnlyList<McpGatewayMarkdownLdGraphDocument> Deserialize(string json)
    {
        try
        {
            var bundle =
                JsonSerializer.Deserialize<Bundle>(json, McpGatewayJsonSerializer.Options)
                ?? throw new InvalidDataException(InvalidGraphFileMessage);
            if (bundle.Version != CurrentVersion)
            {
                throw new InvalidDataException(UnsupportedGraphFileVersionMessage);
            }

            return ValidateDocuments(bundle.Documents);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(InvalidGraphFileMessage, ex);
        }
    }

    internal static IReadOnlyList<MarkdownSourceDocument> ToMarkdownSourceDocuments(
        IEnumerable<McpGatewayMarkdownLdGraphDocument> documents
    )
    {
        return ValidateDocuments(documents.ToArray())
            .Select(static document => new MarkdownSourceDocument(
                document.Path,
                document.Content,
                TryCreateUri(document.CanonicalUri)
            ))
            .ToArray();
    }

    private static IReadOnlyList<McpGatewayMarkdownLdGraphDocument> ValidateDocuments(
        IReadOnlyList<McpGatewayMarkdownLdGraphDocument> documents
    )
    {
        var normalized = new List<McpGatewayMarkdownLdGraphDocument>(documents.Count);
        foreach (var document in documents)
        {
            if (string.IsNullOrWhiteSpace(document.Path))
            {
                throw new InvalidDataException(EmptyDocumentPathMessage);
            }

            if (string.IsNullOrWhiteSpace(document.Content))
            {
                throw new InvalidDataException(EmptyDocumentContentMessage);
            }

            normalized.Add(
                document with
                {
                    Path = document.Path.Replace('\\', '/').TrimStart('/'),
                    Content = document.Content,
                    CanonicalUri = string.IsNullOrWhiteSpace(document.CanonicalUri)
                        ? null
                        : document.CanonicalUri.Trim(),
                }
            );
        }

        return normalized;
    }

    private static Uri? TryCreateUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            ? uri
            : throw new InvalidDataException(InvalidDocumentCanonicalUriMessage);
    }

    private sealed record Bundle(
        int Version,
        IReadOnlyList<McpGatewayMarkdownLdGraphDocument> Documents
    );
}
