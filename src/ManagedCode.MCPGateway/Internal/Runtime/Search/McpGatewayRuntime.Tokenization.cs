using System.Text;

namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime
{
    private TokenSearchProfile BuildTokenSearchProfile(
        IEnumerable<WeightedTextSegment> segments,
        IReadOnlyDictionary<string, double>? inverseDocumentFrequencies = null)
    {
        var termWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in segments)
        {
            if (segment.Weight <= double.Epsilon || string.IsNullOrWhiteSpace(segment.Text))
            {
                continue;
            }

            var tokenTerms = ExtractTokenTerms(segment.Text);
            if (tokenTerms.Count == 0)
            {
                tokenTerms = BuildSearchTerms(segment.Text).ToList();
            }

            foreach (var term in tokenTerms)
            {
                AddWeightedSearchTerm(termWeights, term, segment.Weight);
                AddCharacterNGramTerms(termWeights, term, segment.Weight);
            }
        }

        var rawProfile = CreateTokenSearchProfile(termWeights);
        return inverseDocumentFrequencies is { Count: > 0 }
            ? ApplyTokenInverseDocumentFrequencies(rawProfile, inverseDocumentFrequencies)
            : rawProfile;
    }

    private IReadOnlyList<string> ExtractTokenTerms(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        try
        {
            var terms = new List<string>();
            var tokens = _searchTokenizer.EncodeToTokens(text, out var _);
            foreach (var token in tokens)
            {
                var normalizedTerms = BuildSearchTerms(token.Value);
                if (normalizedTerms.Count == 0)
                {
                    continue;
                }

                terms.AddRange(normalizedTerms);
            }

            return terms;
        }
        catch
        {
            return BuildSearchTerms(text).ToList();
        }
    }

    private static TokenSearchProfile CreateTokenSearchProfile(
        IReadOnlyDictionary<string, double> termWeights)
    {
        if (termWeights.Count == 0)
        {
            return TokenSearchProfile.Empty;
        }

        var magnitudeSquared = 0d;
        var totalWeight = 0d;
        foreach (var weight in termWeights.Values)
        {
            magnitudeSquared += weight * weight;
            totalWeight += weight;
        }

        return new TokenSearchProfile(
            termWeights,
            termWeights.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase),
            Math.Sqrt(magnitudeSquared),
            totalWeight);
    }

    private static IReadOnlySet<string> BuildLexicalTerms(
        IEnumerable<WeightedTextSegment> segments)
    {
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in segments)
        {
            foreach (var term in BuildSearchTerms(segment.Text))
            {
                terms.Add(term);
            }
        }

        return terms;
    }

    private static void AddTokenSearchTextSegment(
        List<WeightedTextSegment> segments,
        string? text,
        double weight)
    {
        if (string.IsNullOrWhiteSpace(text) || weight <= double.Epsilon)
        {
            return;
        }

        segments.Add(new WeightedTextSegment(text.Trim(), weight));
    }

    private static void AddTokenSearchIdentifierSegment(
        List<WeightedTextSegment> segments,
        string? identifier,
        double weight)
    {
        if (string.IsNullOrWhiteSpace(identifier) || weight <= double.Epsilon)
        {
            return;
        }

        var trimmedIdentifier = identifier.Trim();
        segments.Add(new WeightedTextSegment(trimmedIdentifier, weight));

        var humanizedIdentifier = HumanizeIdentifier(trimmedIdentifier);
        if (!string.IsNullOrWhiteSpace(humanizedIdentifier) &&
            !string.Equals(humanizedIdentifier, trimmedIdentifier, StringComparison.OrdinalIgnoreCase))
        {
            segments.Add(new WeightedTextSegment(
                humanizedIdentifier,
                weight * HumanizedIdentifierWeightFactor));
        }
    }

    private static string HumanizeIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(identifier.Length + 8);
        var previousWasSeparator = false;
        var previousWasLowerOrDigit = false;

        foreach (var character in identifier.Trim())
        {
            if (char.IsWhiteSpace(character) ||
                character is '_' or '-' or '.' or ',' or ';' or ':' or '/' or '\\')
            {
                if (builder.Length > 0 && !previousWasSeparator)
                {
                    builder.Append(' ');
                }

                previousWasSeparator = true;
                previousWasLowerOrDigit = false;
                continue;
            }

            if (char.IsUpper(character) && previousWasLowerOrDigit && !previousWasSeparator)
            {
                builder.Append(' ');
            }

            builder.Append(char.ToLowerInvariant(character));
            previousWasSeparator = false;
            previousWasLowerOrDigit = char.IsLower(character) || char.IsDigit(character);
        }

        return builder.ToString().Trim();
    }

    private static HashSet<string> BuildSearchTerms(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in text.Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.Length < 2)
            {
                continue;
            }

            var normalized = token.ToLowerInvariant();
            if (IgnoredSearchTerms.Contains(normalized))
            {
                continue;
            }

            terms.Add(normalized);

            if (normalized.Length > 3 && normalized.EndsWith(PluralSuffixIes, StringComparison.Ordinal))
            {
                terms.Add($"{normalized[..^3]}y");
                continue;
            }

            if (normalized.Length > 3 && normalized.EndsWith(PluralSuffixEs, StringComparison.Ordinal))
            {
                terms.Add(normalized[..^2]);
            }
            else if (normalized.Length > 3 && normalized.EndsWith('s'))
            {
                terms.Add(normalized[..^1]);
            }
        }

        return terms;
    }

    private static void AddWeightedSearchTerm(
        Dictionary<string, double> termWeights,
        string? term,
        double weight)
    {
        if (string.IsNullOrWhiteSpace(term) || weight <= double.Epsilon)
        {
            return;
        }

        termWeights[term] = termWeights.TryGetValue(term, out var existingWeight)
            ? existingWeight + weight
            : weight;
    }

    private static void AddCharacterNGramTerms(
        Dictionary<string, double> termWeights,
        string term,
        double weight)
    {
        var ngrams = BuildCharacterNGramTerms(term);
        if (ngrams.Count == 0)
        {
            return;
        }

        var ngramWeight = (weight * CharacterNGramTokenWeightFactor) / ngrams.Count;
        foreach (var ngram in ngrams)
        {
            AddWeightedSearchTerm(termWeights, ngram, ngramWeight);
        }
    }

    private static IReadOnlyList<string> BuildCharacterNGramTerms(string term)
    {
        if (string.IsNullOrWhiteSpace(term) || term.Length < 5)
        {
            return [];
        }

        var ngrams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index <= term.Length - CharacterNGramLength; index++)
        {
            ngrams.Add($"{CharacterNGramPrefix}{term.Substring(index, CharacterNGramLength)}");
        }

        return ngrams.ToList();
    }
}
