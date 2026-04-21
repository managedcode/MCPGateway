using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime
{
    private static McpGatewayToolSearchHints ResolveSearchHints(
        AITool tool,
        McpGatewayToolSearchHints? registeredHints
    ) => ToolSearchHintResolver.Resolve(tool, registeredHints);

    private static class ToolSearchHintResolver
    {
        public static McpGatewayToolSearchHints Resolve(
            AITool tool,
            McpGatewayToolSearchHints? registeredHints
        )
        {
            var accumulator = new SearchHintAccumulator();
            accumulator.AddRegisteredHints(registeredHints);

            if (tool is McpClientTool mcpTool)
            {
                accumulator.AddAnnotations(mcpTool.ProtocolTool?.Annotations);
                accumulator.AddSerializedHints(mcpTool.ProtocolTool?.Annotations);
            }

            var function = tool as AIFunction ?? tool.GetService<AIFunction>();
            if (function?.AdditionalProperties is { Count: > 0 })
            {
                accumulator.AddSerializedHints(function.AdditionalProperties);
            }

            return accumulator.Build();
        }

        private static IReadOnlyList<string> ReadSearchHintValues(
            JsonElement element,
            params string[] propertyNames
        )
        {
            foreach (var propertyName in propertyNames)
            {
                if (!element.TryGetProperty(propertyName, out var property))
                {
                    continue;
                }

                return ReadSearchHintValues(property);
            }

            return [];
        }

        private static IReadOnlyList<string> ReadSearchHintValues(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => [element.GetString() ?? string.Empty],
                JsonValueKind.Array => element
                    .EnumerateArray()
                    .Where(static item => item.ValueKind == JsonValueKind.String)
                    .Select(static item => item.GetString() ?? string.Empty)
                    .ToArray(),
                _ => [],
            };
        }

        private static void AddSearchHintValues(
            ICollection<string> target,
            ISet<string> seenValues,
            IEnumerable<string>? values
        )
        {
            if (values is null)
            {
                return;
            }

            foreach (var value in values)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var normalized = value.Trim();
                if (!seenValues.Add(normalized))
                {
                    continue;
                }

                target.Add(normalized);
            }
        }

        private static bool? ReadSearchHintBoolean(
            JsonElement element,
            params string[] propertyNames
        )
        {
            foreach (var propertyName in propertyNames)
            {
                if (!element.TryGetProperty(propertyName, out var property))
                {
                    continue;
                }

                return property.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.String
                        when bool.TryParse(property.GetString(), out var parsedValue) =>
                        parsedValue,
                    _ => null,
                };
            }

            return null;
        }

        private static IReadOnlyList<McpGatewayToolExample> ReadUsageExamples(
            JsonElement element,
            params string[] propertyNames
        )
        {
            foreach (var propertyName in propertyNames)
            {
                if (!element.TryGetProperty(propertyName, out var property))
                {
                    continue;
                }

                return ReadUsageExamples(property);
            }

            return [];
        }

        private static IReadOnlyList<McpGatewayToolExample> ReadUsageExamples(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String =>
                [
                    new McpGatewayToolExample(element.GetString() ?? string.Empty),
                ],
                JsonValueKind.Array => element
                    .EnumerateArray()
                    .Select(ReadUsageExample)
                    .Where(static example => example is not null)
                    .Cast<McpGatewayToolExample>()
                    .ToArray(),
                JsonValueKind.Object => ReadUsageExample(element) is { } example ? [example] : [],
                _ => [],
            };
        }

        private static McpGatewayToolExample? ReadUsageExample(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                var value = element.GetString();
                return string.IsNullOrWhiteSpace(value)
                    ? null
                    : new McpGatewayToolExample(value.Trim());
            }

            if (element.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var input = TryReadSearchHintString(
                element,
                UsageExampleInputLabel,
                "input",
                "Input",
                "request",
                "Request"
            );
            if (string.IsNullOrWhiteSpace(input))
            {
                return null;
            }

            var output = TryReadSearchHintString(
                element,
                "output",
                "Output",
                "response",
                "Response"
            );
            var description = TryReadSearchHintString(
                element,
                "description",
                "Description",
                "label",
                "Label"
            );
            return new McpGatewayToolExample(input.Trim(), output?.Trim(), description?.Trim());
        }

        private static string? TryReadSearchHintString(
            JsonElement element,
            params string[] propertyNames
        )
        {
            foreach (var propertyName in propertyNames)
            {
                if (
                    element.TryGetProperty(propertyName, out var property)
                    && property.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(property.GetString())
                )
                {
                    return property.GetString();
                }
            }

            return null;
        }

        private static McpGatewayToolCostTier? ReadCostTier(JsonElement element) =>
            ReadEnumValue<McpGatewayToolCostTier>(
                element,
                CostTierPropertyName,
                CostTierCamelCasePropertyName,
                CostTierSnakeCasePropertyName
            );

        private static McpGatewayToolLatencyTier? ReadLatencyTier(JsonElement element) =>
            ReadEnumValue<McpGatewayToolLatencyTier>(
                element,
                LatencyTierPropertyName,
                LatencyTierCamelCasePropertyName,
                LatencyTierSnakeCasePropertyName
            );

        private static TEnum? ReadEnumValue<TEnum>(
            JsonElement element,
            params string[] propertyNames
        )
            where TEnum : struct
        {
            foreach (var propertyName in propertyNames)
            {
                if (!element.TryGetProperty(propertyName, out var property))
                {
                    continue;
                }

                if (
                    property.ValueKind == JsonValueKind.String
                    && Enum.TryParse<TEnum>(
                        property.GetString(),
                        ignoreCase: true,
                        out var parsedValue
                    )
                )
                {
                    return parsedValue;
                }
            }

            return null;
        }

        private sealed class SearchHintAccumulator
        {
            private readonly List<string> _aliases = [];
            private readonly List<string> _keywords = [];
            private readonly List<string> _categories = [];
            private readonly List<string> _tags = [];
            private readonly List<string> _dataSources = [];
            private readonly List<McpGatewayToolExample> _usageExamples = [];
            private readonly HashSet<string> _seenAliases = new(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> _seenKeywords = new(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> _seenCategories = new(
                StringComparer.OrdinalIgnoreCase
            );
            private readonly HashSet<string> _seenTags = new(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> _seenDataSources = new(
                StringComparer.OrdinalIgnoreCase
            );
            private readonly HashSet<string> _seenUsageExamples = new(
                StringComparer.OrdinalIgnoreCase
            );

            private bool? _readOnly;
            private bool? _idempotent;
            private bool? _destructive;
            private bool? _openWorld;
            private McpGatewayToolCostTier? _costTier;
            private McpGatewayToolLatencyTier? _latencyTier;
            private bool? _enabledByDefault;

            public void AddRegisteredHints(McpGatewayToolSearchHints? searchHints)
            {
                AddSearchHintValues(_aliases, _seenAliases, searchHints?.Aliases);
                AddSearchHintValues(_keywords, _seenKeywords, searchHints?.Keywords);
                AddSearchHintValues(_categories, _seenCategories, searchHints?.Categories);
                AddSearchHintValues(_tags, _seenTags, searchHints?.Tags);
                AddSearchHintValues(_dataSources, _seenDataSources, searchHints?.DataSources);
                AddUsageExamples(searchHints?.UsageExamples);
                _readOnly ??= searchHints?.ReadOnly;
                _idempotent ??= searchHints?.Idempotent;
                _destructive ??= searchHints?.Destructive;
                _openWorld ??= searchHints?.OpenWorld;
                _costTier ??= searchHints?.CostTier;
                _latencyTier ??= searchHints?.LatencyTier;
                _enabledByDefault ??= searchHints?.EnabledByDefault;
            }

            public void AddAnnotations(ToolAnnotations? annotations)
            {
                if (annotations is null)
                {
                    return;
                }

                _readOnly ??= annotations.ReadOnlyHint;
                _idempotent ??= annotations.IdempotentHint;
                _destructive ??= annotations.DestructiveHint;
                _openWorld ??= annotations.OpenWorldHint;
            }

            public void AddSerializedHints(object? value)
            {
                if (
                    McpGatewayJsonSerializer.TrySerializeToElement(value) is not JsonElement element
                    || element.ValueKind != JsonValueKind.Object
                )
                {
                    return;
                }

                AddSearchHintValues(
                    _aliases,
                    _seenAliases,
                    ReadSearchHintValues(
                        element,
                        SearchAliasesPropertyName,
                        SearchAliasesCamelCasePropertyName,
                        SearchAliasesSnakeCasePropertyName,
                        SearchAliasesShortPropertyName
                    )
                );
                AddSearchHintValues(
                    _keywords,
                    _seenKeywords,
                    ReadSearchHintValues(
                        element,
                        SearchKeywordsPropertyName,
                        SearchKeywordsCamelCasePropertyName,
                        SearchKeywordsSnakeCasePropertyName,
                        SearchKeywordsShortPropertyName
                    )
                );
                AddSearchHintValues(
                    _categories,
                    _seenCategories,
                    ReadSearchHintValues(
                        element,
                        CategoriesPropertyName,
                        CategoriesCamelCasePropertyName,
                        CategoriesSnakeCasePropertyName,
                        CategoryPropertyName,
                        CategoryCamelCasePropertyName,
                        CategorySnakeCasePropertyName
                    )
                );
                AddSearchHintValues(
                    _tags,
                    _seenTags,
                    ReadSearchHintValues(
                        element,
                        TagsPropertyName,
                        TagsCamelCasePropertyName,
                        TagsSnakeCasePropertyName
                    )
                );
                AddSearchHintValues(
                    _dataSources,
                    _seenDataSources,
                    ReadSearchHintValues(
                        element,
                        DataSourcesPropertyName,
                        DataSourcesCamelCasePropertyName,
                        DataSourcesSnakeCasePropertyName,
                        DataSourcePropertyName,
                        DataSourceCamelCasePropertyName,
                        DataSourceSnakeCasePropertyName
                    )
                );
                AddUsageExamples(
                    ReadUsageExamples(
                        element,
                        UsageExamplesPropertyName,
                        UsageExamplesCamelCasePropertyName,
                        UsageExamplesSnakeCasePropertyName,
                        ExamplesPropertyName,
                        ExamplesCamelCasePropertyName
                    )
                );
                _readOnly ??= ReadSearchHintBoolean(
                    element,
                    ReadOnlyPropertyName,
                    ReadOnlyCamelCasePropertyName,
                    ReadOnlySnakeCasePropertyName,
                    ReadOnlyHintPropertyName,
                    ReadOnlyHintCamelCasePropertyName,
                    ReadOnlyHintSnakeCasePropertyName
                );
                _idempotent ??= ReadSearchHintBoolean(
                    element,
                    IdempotentPropertyName,
                    IdempotentCamelCasePropertyName,
                    IdempotentSnakeCasePropertyName,
                    IdempotentHintPropertyName,
                    IdempotentHintCamelCasePropertyName,
                    IdempotentHintSnakeCasePropertyName
                );
                _destructive ??= ReadSearchHintBoolean(
                    element,
                    DestructivePropertyName,
                    DestructiveCamelCasePropertyName,
                    DestructiveSnakeCasePropertyName,
                    DestructiveHintPropertyName,
                    DestructiveHintCamelCasePropertyName,
                    DestructiveHintSnakeCasePropertyName
                );
                _openWorld ??= ReadSearchHintBoolean(
                    element,
                    OpenWorldPropertyName,
                    OpenWorldCamelCasePropertyName,
                    OpenWorldSnakeCasePropertyName,
                    OpenWorldHintPropertyName,
                    OpenWorldHintCamelCasePropertyName,
                    OpenWorldHintSnakeCasePropertyName
                );
                _costTier ??= ReadCostTier(element);
                _latencyTier ??= ReadLatencyTier(element);
                _enabledByDefault ??= ReadSearchHintBoolean(
                    element,
                    EnabledByDefaultPropertyName,
                    EnabledByDefaultCamelCasePropertyName,
                    EnabledByDefaultSnakeCasePropertyName,
                    DefaultEnabledPropertyName,
                    DefaultEnabledCamelCasePropertyName,
                    DefaultEnabledSnakeCasePropertyName
                );
            }

            public McpGatewayToolSearchHints Build() =>
                new(
                    _aliases,
                    _keywords,
                    _categories,
                    _tags,
                    _dataSources,
                    _usageExamples,
                    _readOnly,
                    _idempotent,
                    _destructive,
                    _openWorld,
                    _costTier,
                    _latencyTier,
                    _enabledByDefault
                );

            private void AddUsageExamples(IEnumerable<McpGatewayToolExample>? usageExamples)
            {
                if (usageExamples is null)
                {
                    return;
                }

                foreach (var usageExample in usageExamples)
                {
                    if (string.IsNullOrWhiteSpace(usageExample.Input))
                    {
                        continue;
                    }

                    var key = string.Concat(
                        usageExample.Description,
                        "\n",
                        usageExample.Input,
                        "\n",
                        usageExample.Output
                    );
                    if (!_seenUsageExamples.Add(key))
                    {
                        continue;
                    }

                    _usageExamples.Add(
                        new McpGatewayToolExample(
                            usageExample.Input.Trim(),
                            string.IsNullOrWhiteSpace(usageExample.Output)
                                ? null
                                : usageExample.Output.Trim(),
                            string.IsNullOrWhiteSpace(usageExample.Description)
                                ? null
                                : usageExample.Description.Trim()
                        )
                    );
                }
            }
        }
    }
}
