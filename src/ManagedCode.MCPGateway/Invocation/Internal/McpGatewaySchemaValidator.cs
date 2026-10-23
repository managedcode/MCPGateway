using System.Text.Json;
using Json.Schema;

namespace ManagedCode.MCPGateway;

/// <summary>Compile schemas once per catalog entry; never resolve remote references implicitly.</summary>
internal sealed class McpGatewaySchemaValidator(McpGatewayToolDescriptor descriptor)
{
    private const string ReferenceKeyword = "$ref";
    private const string DynamicReferenceKeyword = "$dynamicRef";
    private const string IdKeyword = "$id";
    private const string ExternalReferenceMessage = "External schema references must be bundled before gateway registration.";
    private static readonly HashSet<string> InstanceKeywords = ["const", "enum", "default", "examples"];
    private static readonly HashSet<string> SchemaMapKeywords = ["properties", "patternProperties", "$defs", "definitions", "dependentSchemas", "dependencies"];
    private static readonly Uri BundledSchemaUri = new("https://mcpgateway.invalid/schema/bundled");
    private readonly Lazy<JsonSchema?> _input = new(() => Compile(descriptor.InputSchema));
    private readonly Lazy<JsonSchema?> _output = new(() => descriptor.OutputSchema is { } schema ? Compile(schema) : null);

    public bool ValidateArguments(IReadOnlyDictionary<string, JsonElement> arguments)
    {
        var instance = McpGatewayJsonSerializer.TrySerializeToElement(arguments);
        return instance is { } value && (_input.Value?.Evaluate(value).IsValid ?? true);
    }

    public bool ValidateOutput(object? output)
    {
        try
        { return ValidateOutputCore(output); }
        catch (Exception exception) when (exception is ArgumentException or JsonException or InvalidOperationException or JsonSchemaException or RefResolutionException)
        { return false; }
    }

    private bool ValidateOutputCore(object? output)
    {
        var schema = _output.Value;
        if (schema is null)
        {
            return true;
        }
        // JSON null is a valid instance even though the canonical convenience converter omits it.
        var instance = McpGatewayJsonSerializer.TrySerializeToElement(output)
            ?? JsonSerializer.SerializeToElement<object?>(null, McpGatewayJsonSerializer.Options);
        return schema.Evaluate(instance).IsValid;
    }

    internal static JsonSchema? Compile(JsonElement schema)
    {
        if (schema.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }
        var bundledIds = new HashSet<string>(StringComparer.Ordinal) { DocumentId(BundledSchemaUri) };
        VisitReferences(schema, BundledSchemaUri, bundledIds, validate: false);
        VisitReferences(schema, BundledSchemaUri, bundledIds, validate: true);
        return JsonSchema.FromText(schema.GetRawText(), new BuildOptions
        {
            SchemaRegistry = new SchemaRegistry
            {
                Fetch = static (_, _) => throw new InvalidOperationException("External schema resolution requires an explicitly registered, bundled schema."),
            },
        }, BundledSchemaUri);
    }

    private static void VisitReferences(JsonElement node, Uri baseUri, ISet<string> bundledIds, bool validate, bool schemaMap = false)
    {
        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
            {
                VisitReferences(item, baseUri, bundledIds, validate);
            }
            return;
        }
        if (node.ValueKind != JsonValueKind.Object)
        {
            return;
        }
        if (!schemaMap && node.TryGetProperty(IdKeyword, out var id) && id.ValueKind == JsonValueKind.String
            && Uri.TryCreate(baseUri, id.GetString(), out var resolvedId))
        {
            baseUri = resolvedId;
            bundledIds.Add(DocumentId(resolvedId));
        }
        foreach (var property in node.EnumerateObject())
        {
            if (!schemaMap && InstanceKeywords.Contains(property.Name))
            {
                continue;
            }
            if (validate && !schemaMap && property.Name is ReferenceKeyword or DynamicReferenceKeyword && property.Value.ValueKind == JsonValueKind.String)
            {
                var reference = property.Value.GetString();
                if (reference is not null && (!Uri.TryCreate(baseUri, reference, out var resolved)
                    || !bundledIds.Contains(DocumentId(resolved))))
                {
                    throw new InvalidOperationException(ExternalReferenceMessage);
                }
            }
            VisitReferences(property.Value, baseUri, bundledIds, validate,
                !schemaMap && SchemaMapKeywords.Contains(property.Name));
        }
    }
    private static string DocumentId(Uri uri) => uri.AbsoluteUri.Split('#', 2)[0];
}
