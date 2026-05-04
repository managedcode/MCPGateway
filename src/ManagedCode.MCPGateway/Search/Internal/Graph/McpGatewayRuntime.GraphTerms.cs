using System.Collections.Frozen;

namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime
{
    private static bool ContainsAnyGraphTerm(
        IReadOnlyList<string> terms,
        FrozenSet<string> lookupTerms
    )
    {
        for (var index = 0; index < terms.Count; index++)
        {
            if (lookupTerms.Contains(terms[index]))
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> BuildOrderedGraphTerms(
        string? text,
        int maxTerms = int.MaxValue,
        Func<string, bool>? predicate = null
    )
    {
        if (string.IsNullOrWhiteSpace(text) || maxTerms <= 0)
        {
            return [];
        }

        var capacity = maxTerms == int.MaxValue
            ? GraphDefaultTermCollectionCapacity
            : Math.Min(maxTerms, GraphDefaultTermCollectionCapacity);
        var orderedTerms = new List<string>(capacity);
        var seenTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (
            var token in text.Split(
                TokenSeparators,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            )
        )
        {
            var added = AddGraphTokenTerms(
                orderedTerms,
                seenTerms,
                token,
                maxTerms - orderedTerms.Count,
                predicate
            );
            if (added > 0 && orderedTerms.Count >= maxTerms)
            {
                break;
            }
        }

        return orderedTerms;
    }

    private static int AddGraphTokenTerms(
        ICollection<string> orderedTerms,
        ISet<string> seenTerms,
        string token,
        int remainingTerms,
        Func<string, bool>? predicate
    )
    {
        if (remainingTerms <= 0 || token.Length < GraphMinimumTermLength)
        {
            return 0;
        }

        var normalized = token.ToLowerInvariant();
        if (IgnoredSearchTerms.Contains(normalized))
        {
            return 0;
        }

        var added = AddGraphTerm(orderedTerms, seenTerms, normalized, predicate) ? 1 : 0;
        if (added >= remainingTerms)
        {
            return added;
        }

        var singular = NormalizeGraphPluralTerm(normalized);
        if (
            !string.Equals(singular, normalized, StringComparison.OrdinalIgnoreCase)
            && AddGraphTerm(orderedTerms, seenTerms, singular, predicate)
        )
        {
            added++;
        }

        return added;
    }

    private static bool AddGraphTerm(
        ICollection<string> orderedTerms,
        ISet<string> seenTerms,
        string term,
        Func<string, bool>? predicate
    )
    {
        if (predicate is not null && !predicate(term))
        {
            return false;
        }

        if (!seenTerms.Add(term))
        {
            return false;
        }

        orderedTerms.Add(term);
        return true;
    }

    private static string NormalizeGraphPluralTerm(string normalized)
    {
        if (
            normalized.Length > GraphPluralNormalizationMinimumLength
            && normalized.EndsWith(PluralSuffixIes, StringComparison.Ordinal)
        )
        {
            return $"{normalized[..^3]}y";
        }

        if (
            normalized.Length > GraphPluralNormalizationMinimumLength
            && normalized.EndsWith(PluralSuffixEs, StringComparison.Ordinal)
        )
        {
            return normalized[..^2];
        }

        return normalized.Length > GraphPluralNormalizationMinimumLength
            && normalized.EndsWith('s')
            ? normalized[..^1]
            : normalized;
    }
}
