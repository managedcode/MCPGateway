namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime
{
    private static double ApplySearchBoosts(ToolCatalogEntry entry, string boostQuery, double score)
    {
        if (string.IsNullOrWhiteSpace(boostQuery))
        {
            return score;
        }

        var queryTerms = BuildOrderedGraphTerms(boostQuery)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (queryTerms.Count == 0)
        {
            return score;
        }

        var descriptorTerms = BuildOrderedGraphTerms(
                string.Concat(
                    entry.Descriptor.ToolName,
                    " ",
                    entry.Descriptor.DisplayName,
                    " ",
                    entry.Descriptor.Description,
                    " ",
                    string.Join(" ", entry.Descriptor.SearchAliases),
                    " ",
                    string.Join(" ", entry.Descriptor.SearchKeywords),
                    " ",
                    string.Join(" ", entry.Descriptor.RequiredArguments)
                )
            )
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (descriptorTerms.Count == 0)
        {
            return score;
        }

        var overlap = 0;
        foreach (var queryTerm in queryTerms)
        {
            if (descriptorTerms.Contains(queryTerm))
            {
                overlap++;
            }
        }

        if (overlap == 0)
        {
            return score;
        }

        var coverage = (double)overlap / queryTerms.Count;
        return Math.Min(1d, score + (coverage * ToolNameSignalWeight));
    }
}
