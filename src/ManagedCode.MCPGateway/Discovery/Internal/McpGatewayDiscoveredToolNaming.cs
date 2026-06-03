namespace ManagedCode.MCPGateway;

internal static class McpGatewayDiscoveredToolNaming
{
    private const string DefaultToolName = "gateway_tool";
    private const string NameSeparator = "_";
    private const string DescriptionPrefix = "Direct proxy for gateway tool ";
    private const string ToolIdLabel = " (";
    private const string DescriptionSeparator = "). ";
    private const string CategoriesLabel = "Categories: ";
    private const string TagsLabel = "Tags: ";
    private const string DataSourcesLabel = "Data sources: ";
    private const string RequiredArgumentsLabel = "Required arguments: ";
    private const string ExecutionHintsLabel = "Execution hints: ";
    private const string UsageExampleLabel = "Example input: ";
    private const string ArgumentsHint =
        "Pass named inputs via 'arguments' and use 'query' for free-text tool inputs when supported.";

    public static string BuildDescription(McpGatewaySearchMatch match)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append(DescriptionPrefix);
        builder.Append(match.ToolName);
        builder.Append(ToolIdLabel);
        builder.Append(match.ToolId);
        builder.Append(DescriptionSeparator);
        builder.Append(match.Description);

        AppendValues(builder, CategoriesLabel, match.Categories);
        AppendValues(builder, TagsLabel, match.Tags);
        AppendValues(builder, DataSourcesLabel, match.DataSources);

        if (match.RequiredArguments.Count > 0)
        {
            builder.Append(' ');
            builder.Append(RequiredArgumentsLabel);
            builder.Append(BuildRequiredArgumentList(match.RequiredArguments));
            builder.Append('.');
        }

        var executionHints = BuildExecutionHints(match);
        if (executionHints.Length > 0)
        {
            builder.Append(' ');
            builder.Append(ExecutionHintsLabel);
            builder.Append(executionHints);
            builder.Append('.');
        }

        if (match.UsageExamples.Count > 0)
        {
            builder.Append(' ');
            builder.Append(UsageExampleLabel);
            builder.Append(match.UsageExamples[0].Input);
            builder.Append('.');
        }

        builder.Append(' ');
        builder.Append(ArgumentsHint);
        return builder.ToString();
    }

    public static string CreateName(McpGatewaySearchMatch match, ISet<string> reservedNames)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(reservedNames);

        var sanitizedToolName = McpGatewayProtocolName.Normalize(match.ToolName, DefaultToolName);
        if (reservedNames.Add(sanitizedToolName))
        {
            return sanitizedToolName;
        }

        var compositeName = McpGatewayProtocolName.CreateSourceQualifiedName(
            match.SourceId,
            sanitizedToolName,
            DefaultToolName
        );
        if (reservedNames.Add(compositeName))
        {
            return compositeName;
        }

        for (var suffix = 2; ; suffix++)
        {
            var uniqueName = $"{compositeName}{NameSeparator}{suffix}";
            if (reservedNames.Add(uniqueName))
            {
                return uniqueName;
            }
        }
    }

    private static string BuildRequiredArgumentList(IReadOnlyList<string> requiredArguments)
    {
        if (requiredArguments.Count == 1)
        {
            return requiredArguments[0];
        }

        var builder = new System.Text.StringBuilder();
        for (var index = 0; index < requiredArguments.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(", ");
            }

            builder.Append(requiredArguments[index]);
        }

        return builder.ToString();
    }

    private static void AppendValues(
        System.Text.StringBuilder builder,
        string label,
        IReadOnlyList<string> values
    )
    {
        if (values.Count == 0)
        {
            return;
        }

        builder.Append(' ');
        builder.Append(label);
        builder.Append(BuildRequiredArgumentList(values));
        builder.Append('.');
    }

    private static string BuildExecutionHints(McpGatewaySearchMatch match)
    {
        var parts = new List<string>();

        if (match.IsReadOnly is not null)
        {
            parts.Add(match.IsReadOnly.Value ? "read-only" : "writes state");
        }

        if (match.IsIdempotent is true)
        {
            parts.Add("idempotent");
        }

        if (match.IsDestructive is true)
        {
            parts.Add("destructive");
        }

        if (match.CostTier is not null)
        {
            parts.Add($"cost {match.CostTier.Value.ToString().ToLowerInvariant()}");
        }

        if (match.LatencyTier is not null)
        {
            parts.Add($"latency {match.LatencyTier.Value.ToString().ToLowerInvariant()}");
        }

        return string.Join(", ", parts);
    }

}
