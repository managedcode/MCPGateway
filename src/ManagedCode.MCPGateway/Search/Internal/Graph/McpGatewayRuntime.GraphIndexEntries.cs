using ManagedCode.MarkdownLd.Kb.Pipeline;

namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime
{
    private static IReadOnlyDictionary<string, ToolCatalogEntry> CreateEntriesByGraphNodeId(
        IReadOnlyList<ToolCatalogEntry> entries,
        IReadOnlyList<MarkdownDocument> documents
    )
    {
        var entriesByNodeId = new Dictionary<string, ToolCatalogEntry>(StringComparer.Ordinal);
        var entriesByExpectedUri = new Dictionary<string, ToolCatalogEntry>(StringComparer.Ordinal);
        var entriesBySourcePath = new Dictionary<string, ToolCatalogEntry>(
            StringComparer.OrdinalIgnoreCase
        );
        var entriesByToolName = entries
            .GroupBy(static entry => entry.Descriptor.ToolName, StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() == 1)
            .ToDictionary(
                static group => group.Key,
                static group => group.Single(),
                StringComparer.OrdinalIgnoreCase
            );

        foreach (var entry in entries)
        {
            var expectedUri = CreateToolGraphDocumentUri(entry.Descriptor).AbsoluteUri;
            entriesByExpectedUri[expectedUri] = entry;
            entriesByNodeId[expectedUri] = entry;
            entriesBySourcePath[CreateToolGraphSourcePath(entry.Descriptor)] = entry;
        }

        foreach (var document in documents)
        {
            if (
                entriesByExpectedUri.TryGetValue(
                    document.DocumentUri.AbsoluteUri,
                    out var expectedEntry
                )
                || entriesBySourcePath.TryGetValue(
                    NormalizeGraphSourcePath(document.SourcePath),
                    out expectedEntry
                )
                || TryResolveEntryByGraphFileName(
                    entriesByToolName,
                    document.SourcePath,
                    out expectedEntry
                )
                || TryResolveSingleEntry(entries, out expectedEntry)
            )
            {
                entriesByNodeId[document.DocumentUri.AbsoluteUri] = expectedEntry;
            }
        }

        return entriesByNodeId;
    }

    private static bool TryResolveEntryByGraphFileName(
        IReadOnlyDictionary<string, ToolCatalogEntry> entriesByToolName,
        string sourcePath,
        out ToolCatalogEntry entry
    )
    {
        var toolName = Path.GetFileNameWithoutExtension(sourcePath);
        if (
            !string.IsNullOrWhiteSpace(toolName)
            && entriesByToolName.TryGetValue(toolName, out entry!)
        )
        {
            return true;
        }

        entry = null!;
        return false;
    }

    private static bool TryResolveSingleEntry(
        IReadOnlyList<ToolCatalogEntry> entries,
        out ToolCatalogEntry entry
    )
    {
        if (entries.Count == 1)
        {
            entry = entries[0];
            return true;
        }

        entry = null!;
        return false;
    }

    private static string NormalizeGraphSourcePath(string sourcePath) =>
        sourcePath.Replace('\\', '/').TrimStart('/');
}
