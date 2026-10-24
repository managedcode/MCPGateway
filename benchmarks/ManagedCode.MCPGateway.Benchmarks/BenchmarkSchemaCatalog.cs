using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Benchmarks;

/// <summary>
/// Immutable schema-heavy catalog shaped like a database server tool surface: 70 tools across twelve
/// categories, each with a closed JSON input schema, an output schema and explicit search hints.
/// </summary>
internal static class BenchmarkSchemaCatalog
{
    public const int ToolCount = 70;
    public const int DefaultSearchLimit = 3;
    public const int MaxSearchResults = 4;
    public const int MaxDescriptorLength = 4_096;
    public const string SourceId = "database";
    public const string DocumentQuery = "read one document by key";
    public const string SeriesQuery = "aggregate time series samples in windows";

    private static readonly (string Category, string Prefix, string Noun)[] Categories =
    [
        ("documents", "documents", "document"),
        ("event-streams", "streams", "stream"),
        ("events", "events", "event"),
        ("graph", "graph", "graph edge"),
        ("time-series", "series", "series sample"),
        ("sql", "query", "query page"),
        ("change-feed", "changes", "change"),
        ("projections", "projections", "projection batch"),
        ("messaging", "messages", "message"),
        ("resources", "resources", "resource"),
        ("search", "search", "search hit"),
        ("administration", "admin", "database setting"),
    ];

    private static readonly (string Verb, bool ReadOnly, bool Idempotent, bool Destructive)[] Verbs =
    [
        ("get", true, true, false),
        ("list", true, true, false),
        ("put", false, true, false),
        ("append", false, false, false),
        ("delete", false, true, true),
        ("inspect", true, true, false),
    ];

    private static readonly string[] ReadRequired = ["database", "key"];
    private static readonly string[] WriteRequired = ["database", "key", "commandId", "payload"];
    private static readonly string[] ConsistencyValues = ["committed", "local", "snapshot"];
    private static readonly IReadOnlyList<SchemaTool> Tools = CreateTools(revision: 0);

    /// <summary>Creates a host for the catalog; a non-zero revision changes every description, so its graph differs.</summary>
    public static ServiceProvider CreateServiceProvider(int revision = 0)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMcpGateway(options => Configure(options, revision));
        return services.BuildServiceProvider();
    }

    public static void Configure(McpGatewayOptions options, int revision = 0)
    {
        options.SearchStrategy = McpGatewaySearchStrategy.Graph;
        options.MarkdownLdGraphSearchMode = McpGatewayMarkdownLdGraphSearchMode.SchemaAware;
        options.MarkdownLdGraphSource = McpGatewayMarkdownLdGraphSource.GeneratedToolGraph;
        options.SearchQueryNormalization = McpGatewaySearchQueryNormalization.Disabled;
        options.DefaultSearchLimit = DefaultSearchLimit;
        options.MaxSearchResults = MaxSearchResults;
        options.MaxDescriptorLength = MaxDescriptorLength;
        foreach (var tool in revision == 0 ? Tools : CreateTools(revision))
        {
            options.AddTool(new SchemaFunction(tool), tool.Hints, SourceId);
        }
    }

    private static List<SchemaTool> CreateTools(int revision)
    {
        var tools = new List<SchemaTool>(ToolCount);
        for (var index = 0; index < ToolCount; index++)
        {
            var (category, prefix, noun) = Categories[index % Categories.Length];
            var (verb, readOnly, idempotent, destructive) = Verbs[index / Categories.Length % Verbs.Length];
            var name = $"db_{prefix}_{verb}";
            var description =
                $"{char.ToUpperInvariant(verb[0])}{verb[1..]} {noun} records in the {category} model of one database. "
                + $"Requires an authorized database name and returns bounded {noun} pages with a continuation cursor."
                + (revision == 0 ? string.Empty : $" Catalog revision {revision}.");
            var hints = new McpGatewayToolSearchHints(
                Aliases: [$"{prefix} {verb}", $"{prefix}/{verb}", category],
                Keywords: [],
                Categories: [category],
                Tags: [],
                DataSources: [],
                UsageExamples: [],
                ReadOnly: readOnly,
                Idempotent: idempotent,
                Destructive: destructive,
                OpenWorld: false,
                EnabledByDefault: true
            );
            tools.Add(new SchemaTool(name, description, CreateInputSchema(noun, readOnly), CreateOutputSchema(noun), hints));
        }

        return tools;
    }

    private static JsonElement CreateInputSchema(string noun, bool readOnly) =>
        JsonSerializer.SerializeToElement(
            new Dictionary<string, object>
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["required"] = readOnly ? ReadRequired : WriteRequired,
                ["properties"] = new Dictionary<string, object>
                {
                    ["database"] = new { type = "string", description = "Authorized database name.", maxLength = 128 },
                    ["key"] = new { type = "string", description = $"Exact {noun} key.", maxLength = 512 },
                    ["commandId"] = new { type = "string", description = "Stable command identity used to deduplicate retries.", format = "uuid" },
                    ["limit"] = new { type = "integer", description = "Maximum page size.", minimum = 1, maximum = 500 },
                    ["cursor"] = new { type = "string", description = "Continuation cursor returned by the previous page.", maxLength = 1024 },
                    ["consistency"] = new { type = "string", @enum = ConsistencyValues, description = "Read cut." },
                    ["fields"] = new { type = "array", items = new { type = "string", maxLength = 128 }, maxItems = 32 },
                    ["payload"] = new
                    {
                        type = "object",
                        description = $"Exact {noun} content.",
                        properties = new Dictionary<string, object>
                        {
                            ["contentType"] = new { type = "string", maxLength = 128 },
                            ["body"] = new { type = "string", maxLength = 65_536 },
                            ["expectedVersion"] = new { type = "integer", minimum = 0 },
                        },
                    },
                    ["timeoutMilliseconds"] = new { type = "integer", minimum = 1, maximum = 60_000 },
                },
            }
        );

    private static JsonElement CreateOutputSchema(string noun) =>
        JsonSerializer.SerializeToElement(
            new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object>
                {
                    ["items"] = new
                    {
                        type = "array",
                        description = $"Returned {noun} records.",
                        items = new
                        {
                            type = "object",
                            properties = new Dictionary<string, object>
                            {
                                ["key"] = new { type = "string" },
                                ["version"] = new { type = "integer" },
                                ["content"] = new { type = "string" },
                            },
                        },
                    },
                    ["cursor"] = new { type = "string" },
                    ["readCut"] = new { type = "integer" },
                },
            }
        );

    private sealed record SchemaTool(
        string Name,
        string Description,
        JsonElement InputSchema,
        JsonElement OutputSchema,
        McpGatewayToolSearchHints Hints
    );

    private sealed class SchemaFunction(SchemaTool tool) : AIFunction
    {
        public override string Name => tool.Name;

        public override string Description => tool.Description;

        public override JsonElement JsonSchema => tool.InputSchema;

        public override JsonElement? ReturnJsonSchema => tool.OutputSchema;

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult<object?>(tool.Name);
    }
}
