using System.Text;

namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime
{
    private static IReadOnlyList<McpGatewayMarkdownLdGraphDocument> CreateMarkdownLdGraphFileDocuments(
        IReadOnlyList<ToolGraphDocumentSource> graphDocuments
    )
    {
        var documents = new List<McpGatewayMarkdownLdGraphDocument>(graphDocuments.Count);
        foreach (var graphDocument in graphDocuments)
        {
            documents.Add(
                new McpGatewayMarkdownLdGraphDocument(
                    graphDocument.SourcePath,
                    BuildToolGraphMarkdown(
                        graphDocument,
                        SelectRelatedToolUris(graphDocument, graphDocuments),
                        SelectNextStepToolUris(graphDocument, graphDocuments)
                    ),
                    graphDocument.DocumentUri.AbsoluteUri
                )
            );
        }

        return documents;
    }

    private static IReadOnlyList<ToolGraphDocumentSource> CreateToolGraphDocumentSources(
        IReadOnlyList<(McpGatewayToolDescriptor Descriptor, string Document)> descriptors
    )
    {
        var documents = new List<ToolGraphDocumentSource>(descriptors.Count);
        foreach (var (descriptor, document) in descriptors)
        {
            documents.Add(
                new ToolGraphDocumentSource(
                    descriptor,
                    document,
                    CreateToolGraphDocumentUri(descriptor),
                    CreateToolGraphSourcePath(descriptor),
                    BuildToolGraphGroups(descriptor),
                    ResolveToolGraphOperation(descriptor)
                )
            );
        }

        return documents;
    }

    private static string BuildToolGraphMarkdown(
        ToolGraphDocumentSource graphDocument,
        IReadOnlyList<string> relatedUris,
        IReadOnlyList<string> nextStepUris
    )
    {
        var descriptor = graphDocument.Descriptor;
        var title = ResolveToolGraphTitle(descriptor);
        var builder = new StringBuilder();

        AppendToolGraphFrontMatter(builder, graphDocument, title, relatedUris, nextStepUris);
        builder.AppendLine();
        builder.Append(GraphMarkdownHeadingPrefix);
        builder.AppendLine(NormalizeGraphLine(title));
        builder.AppendLine();
        builder.AppendLine(GraphMarkdownToolIdentityHeading);
        AppendGraphLine(builder, GraphToolIdLabel, descriptor.ToolId);
        AppendGraphLine(builder, GraphSourceIdLabel, descriptor.SourceId);
        AppendGraphLine(builder, GraphSourceKindLabel, descriptor.SourceKind.ToString());
        builder.AppendLine();
        builder.AppendLine(GraphMarkdownExecutionContractHeading);
        builder.AppendLine(graphDocument.Document);

        if (descriptor.InputSchemaJson is null)
        {
            return builder.ToString();
        }

        builder.AppendLine();
        builder.AppendLine(GraphMarkdownInputSchemaHeading);
        builder.AppendLine(GraphMarkdownJsonFenceStart);
        builder.AppendLine(descriptor.InputSchemaJson);
        builder.AppendLine(GraphMarkdownFenceEnd);

        return builder.ToString();
    }

    private static void AppendToolGraphFrontMatter(
        StringBuilder builder,
        ToolGraphDocumentSource graphDocument,
        string title,
        IReadOnlyList<string> relatedUris,
        IReadOnlyList<string> nextStepUris
    )
    {
        var descriptor = graphDocument.Descriptor;
        builder.AppendLine(GraphMarkdownFrontMatterDelimiter);
        AppendYamlScalar(builder, GraphYamlTitleKey, title);
        AppendYamlScalar(builder, GraphYamlSummaryKey, descriptor.Description);
        AppendYamlScalar(builder, GraphYamlDescriptionKey, descriptor.Description);
        AppendYamlList(builder, GraphYamlTagsKey, BuildToolGraphTags(descriptor));
        AppendYamlList(builder, GraphYamlGroupsKey, graphDocument.Groups.ToArray());
        AppendYamlList(builder, GraphYamlRelatedKey, relatedUris);
        AppendYamlList(builder, GraphYamlNextStepsKey, nextStepUris);
        builder.AppendLine(GraphMarkdownFrontMatterDelimiter);
    }

    private static IReadOnlyList<string> SelectRelatedToolUris(
        ToolGraphDocumentSource document,
        IReadOnlyList<ToolGraphDocumentSource> documents
    ) =>
        documents
            .Where(candidate =>
                !ReferenceEquals(candidate, document) && SharesToolGraphGroup(document, candidate)
            )
            .OrderByDescending(candidate => CalculateToolGraphAffinity(document, candidate))
            .ThenBy(candidate => candidate.Descriptor.ToolName, StringComparer.OrdinalIgnoreCase)
            .Take(GraphMaxRelatedToolsPerDocument)
            .Select(static candidate => candidate.DocumentUri.AbsoluteUri)
            .ToArray();

    private static IReadOnlyList<string> SelectNextStepToolUris(
        ToolGraphDocumentSource document,
        IReadOnlyList<ToolGraphDocumentSource> documents
    )
    {
        if (
            !string.Equals(
                document.Operation,
                GraphRelatedOperationDiscover,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return [];
        }

        return documents
            .Where(candidate =>
                !ReferenceEquals(candidate, document)
                && string.Equals(
                    candidate.Operation,
                    GraphRelatedOperationInspect,
                    StringComparison.OrdinalIgnoreCase
                )
                && SharesToolGraphGroup(document, candidate)
            )
            .OrderByDescending(candidate => CalculateToolGraphAffinity(document, candidate))
            .ThenBy(candidate => candidate.Descriptor.ToolName, StringComparer.OrdinalIgnoreCase)
            .Take(GraphMaxNextStepToolsPerDocument)
            .Select(static candidate => candidate.DocumentUri.AbsoluteUri)
            .ToArray();
    }

    private static bool SharesToolGraphGroup(
        ToolGraphDocumentSource left,
        ToolGraphDocumentSource right
    )
    {
        foreach (var group in left.Groups)
        {
            if (right.Groups.Contains(group))
            {
                return true;
            }
        }

        return false;
    }

    private static int CalculateToolGraphAffinity(
        ToolGraphDocumentSource left,
        ToolGraphDocumentSource right
    )
    {
        var score = string.Equals(
            left.Descriptor.SourceId,
            right.Descriptor.SourceId,
            StringComparison.OrdinalIgnoreCase
        )
            ? 2
            : 0;

        foreach (var group in left.Groups)
        {
            if (right.Groups.Contains(group))
            {
                score++;
            }
        }

        return score;
    }

    private static IReadOnlyList<string> BuildToolGraphTags(McpGatewayToolDescriptor descriptor)
    {
        var tags = new List<string>
        {
            GraphTagGatewayTool,
            string.Concat(GraphTagSourcePrefix, descriptor.SourceId),
            string.Concat(GraphTagKindPrefix, descriptor.SourceKind.ToString()),
        };

        foreach (var requiredArgument in descriptor.RequiredArguments)
        {
            tags.Add(string.Concat(GraphTagArgumentPrefix, requiredArgument));
        }

        foreach (var alias in descriptor.SearchAliases)
        {
            tags.Add(string.Concat(GraphTagAliasPrefix, alias));
        }

        foreach (var keyword in descriptor.SearchKeywords)
        {
            tags.Add(string.Concat(GraphTagKeywordPrefix, keyword));
        }

        foreach (var category in descriptor.Categories)
        {
            tags.Add(string.Concat(GraphTagCategoryPrefix, category));
        }

        foreach (var tag in descriptor.Tags)
        {
            tags.Add(string.Concat(GraphTagMetadataPrefix, tag));
        }

        foreach (var dataSource in descriptor.DataSources)
        {
            tags.Add(string.Concat(GraphTagDataSourcePrefix, dataSource));
        }

        if (descriptor.IsReadOnly is not null)
        {
            tags.Add(
                string.Concat(
                    GraphTagReadOnlyPrefix,
                    descriptor.IsReadOnly.Value ? bool.TrueString : bool.FalseString
                )
            );
        }

        if (descriptor.IsIdempotent is not null)
        {
            tags.Add(
                string.Concat(
                    GraphTagIdempotentPrefix,
                    descriptor.IsIdempotent.Value ? bool.TrueString : bool.FalseString
                )
            );
        }

        if (descriptor.IsDestructive is not null)
        {
            tags.Add(
                string.Concat(
                    GraphTagDestructivePrefix,
                    descriptor.IsDestructive.Value ? bool.TrueString : bool.FalseString
                )
            );
        }

        if (descriptor.IsOpenWorld is not null)
        {
            tags.Add(
                string.Concat(
                    GraphTagOpenWorldPrefix,
                    descriptor.IsOpenWorld.Value ? bool.TrueString : bool.FalseString
                )
            );
        }

        if (descriptor.CostTier is not null)
        {
            tags.Add(string.Concat(GraphTagCostTierPrefix, descriptor.CostTier.Value));
        }

        if (descriptor.LatencyTier is not null)
        {
            tags.Add(string.Concat(GraphTagLatencyTierPrefix, descriptor.LatencyTier.Value));
        }

        tags.Add(
            string.Concat(
                GraphTagEnabledPrefix,
                descriptor.IsEnabledByDefault ? bool.TrueString : bool.FalseString
            )
        );

        return tags;
    }

    private static IReadOnlySet<string> BuildToolGraphGroups(McpGatewayToolDescriptor descriptor)
    {
        var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var capabilityTerms = ExtractToolGraphCapabilityTerms(descriptor);

        foreach (var term in capabilityTerms)
        {
            groups.Add(string.Concat(GraphGroupDomainPrefix, term));
            groups.Add(string.Concat(GraphGroupSourceDomainPrefix, descriptor.SourceId, ":", term));
        }

        foreach (var category in descriptor.Categories)
        {
            groups.Add(string.Concat(GraphGroupCategoryPrefix, category));
        }

        return groups;
    }

    private static IReadOnlyList<string> ExtractToolGraphCapabilityTerms(
        McpGatewayToolDescriptor descriptor
    )
    {
        var terms = new List<string>();
        AddGraphCapabilityTerms(terms, descriptor.ToolName, maxTerms: 3);
        AddGraphCapabilityTerms(terms, descriptor.DisplayName, maxTerms: 2);
        AddGraphCapabilityTerms(terms, string.Join(' ', descriptor.SearchAliases), maxTerms: 3);
        AddGraphCapabilityTerms(terms, string.Join(' ', descriptor.SearchKeywords), maxTerms: 3);
        AddGraphCapabilityTerms(terms, string.Join(' ', descriptor.Categories), maxTerms: 3);
        AddGraphCapabilityTerms(terms, string.Join(' ', descriptor.Tags), maxTerms: 3);
        AddGraphCapabilityTerms(terms, string.Join(' ', descriptor.DataSources), maxTerms: 2);

        if (terms.Count == 0)
        {
            AddGraphCapabilityTerms(terms, descriptor.Description, maxTerms: 3);
        }

        return terms.Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToArray();
    }

    private static void AddGraphCapabilityTerms(
        ICollection<string> terms,
        string? value,
        int maxTerms
    )
    {
        var added = 0;
        foreach (var term in BuildOrderedGraphTerms(HumanizeIdentifier(value ?? string.Empty)))
        {
            if (IsGraphCapabilityTerm(term))
            {
                terms.Add(term);
                added++;
            }

            if (added >= maxTerms)
            {
                return;
            }
        }
    }

    private static bool IsGraphCapabilityTerm(string term) =>
        !GraphGenericTerms.Contains(term)
        && !GraphDiscoveryTerms.Contains(term)
        && !GraphInspectionTerms.Contains(term)
        && !GraphActionTerms.Contains(term);

    private static string ResolveToolGraphOperation(McpGatewayToolDescriptor descriptor)
    {
        var identityTerms = BuildOrderedGraphTerms(
                string.Concat(
                    HumanizeIdentifier(descriptor.ToolName),
                    " ",
                    HumanizeIdentifier(descriptor.DisplayName ?? string.Empty)
                )
            )
            .ToArray();

        if (identityTerms.Any(GraphInspectionTerms.Contains))
        {
            return GraphRelatedOperationInspect;
        }

        if (identityTerms.Any(GraphActionTerms.Contains))
        {
            return GraphRelatedOperationAct;
        }

        if (identityTerms.Any(GraphDiscoveryTerms.Contains))
        {
            return GraphRelatedOperationDiscover;
        }

        var descriptionTerms = BuildOrderedGraphTerms(descriptor.Description).ToArray();
        if (descriptionTerms.Any(GraphDiscoveryTerms.Contains))
        {
            return GraphRelatedOperationDiscover;
        }

        if (descriptionTerms.Any(GraphInspectionTerms.Contains))
        {
            return GraphRelatedOperationInspect;
        }

        if (descriptionTerms.Any(GraphActionTerms.Contains))
        {
            return GraphRelatedOperationAct;
        }

        return GraphRelatedOperationOther;
    }

    private static IEnumerable<string> BuildOrderedGraphTerms(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (
            var token in text.Split(
                TokenSeparators,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            )
        )
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

            if (terms.Add(normalized))
            {
                yield return normalized;
            }

            var singular = NormalizeGraphPluralTerm(normalized);
            if (
                !string.Equals(singular, normalized, StringComparison.OrdinalIgnoreCase)
                && terms.Add(singular)
            )
            {
                yield return singular;
            }
        }
    }

    private static string NormalizeGraphPluralTerm(string normalized)
    {
        if (normalized.Length > 3 && normalized.EndsWith(PluralSuffixIes, StringComparison.Ordinal))
        {
            return $"{normalized[..^3]}y";
        }

        if (normalized.Length > 3 && normalized.EndsWith(PluralSuffixEs, StringComparison.Ordinal))
        {
            return normalized[..^2];
        }

        return normalized.Length > 3 && normalized.EndsWith('s') ? normalized[..^1] : normalized;
    }

    private static void AppendGraphLine(StringBuilder builder, string label, string value)
    {
        builder.Append(GraphMarkdownListPrefix);
        builder.Append(label);
        builder.AppendLine(NormalizeGraphLine(value));
    }

    private static void AppendYamlScalar(StringBuilder builder, string key, string value)
    {
        builder.Append(key);
        builder.Append(": ");
        builder.AppendLine(EscapeYamlDoubleQuotedScalar(value));
    }

    private static void AppendYamlList(
        StringBuilder builder,
        string key,
        IReadOnlyList<string> values
    )
    {
        if (values.Count == 0)
        {
            return;
        }

        builder.Append(key);
        builder.AppendLine(":");
        foreach (var value in values)
        {
            builder.Append("  - ");
            builder.AppendLine(EscapeYamlDoubleQuotedScalar(value));
        }
    }

    private static Uri CreateToolGraphDocumentUri(McpGatewayToolDescriptor descriptor) =>
        new(
            string.Concat(
                GraphKnowledgeBaseUriText,
                GraphToolDocumentUriPrefix,
                Uri.EscapeDataString(descriptor.SourceId),
                "/",
                Uri.EscapeDataString(descriptor.ToolName),
                "/"
            ),
            UriKind.Absolute
        );

    private static string CreateToolGraphSourcePath(McpGatewayToolDescriptor descriptor) =>
        string.Concat(
            GraphToolDocumentPathPrefix,
            Uri.EscapeDataString(descriptor.SourceId),
            "/",
            Uri.EscapeDataString(descriptor.ToolName),
            GraphToolDocumentExtension
        );

    private static string ResolveToolGraphTitle(McpGatewayToolDescriptor descriptor) =>
        string.IsNullOrWhiteSpace(descriptor.DisplayName)
            ? descriptor.ToolName
            : descriptor.DisplayName;

    private static string NormalizeGraphLine(string value) => value.ReplaceLineEndings(" ").Trim();

    private static string EscapeYamlDoubleQuotedScalar(string value)
    {
        var normalized = NormalizeGraphLine(value);
        var builder = new StringBuilder(normalized.Length + 2);
        builder.Append('"');
        foreach (var character in normalized)
        {
            if (character is '\\' or '"')
            {
                builder.Append('\\');
            }

            builder.Append(character);
        }

        builder.Append('"');
        return builder.ToString();
    }
}
