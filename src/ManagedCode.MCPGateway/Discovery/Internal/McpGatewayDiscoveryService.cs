using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayDiscoveryService(IMcpGateway gateway, IServiceProvider? services = null,
    int? maximumSchemaBytes = null) : IMcpGatewayDiscovery
{
    internal const string PrimaryRole = "primary";
    private const string RelatedRole = "related";
    private const string NextStepRole = "nextStep";
    private const int PurposeLength = 256;
    private const int MaximumContinuationLength = 4096;

    public async Task<McpGatewayToolSchemaResult?> GetToolSchemaAsync(string toolId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        var descriptor = gateway is IMcpGatewayToolSchemaLookup lookup
            ? await lookup.GetAuthorizedSchemaToolAsync(toolId, cancellationToken)
            : (await gateway.ListToolsAsync(cancellationToken)).FirstOrDefault(tool => tool.ToolId.Equals(toolId, StringComparison.OrdinalIgnoreCase));
        if (descriptor is not null && gateway is not IMcpGatewayToolSchemaLookup && services is not null)
        {
            await McpGatewayAccessBoundary.DemandAsync(services, McpGatewayOperation.InspectSchema, descriptor.ToolId, descriptor.SourceId, cancellationToken);
        }
        if (descriptor is null)
        { return null; }
        var result = new McpGatewayToolSchemaResult(descriptor, SchemaHash(descriptor.InputSchema, descriptor.OutputSchema),
            descriptor.DefinitionHash ?? McpGatewayDefinitionHash.Create(descriptor, descriptor.BindingFingerprint ?? string.Empty).DefinitionHash);
        var limit = maximumSchemaBytes ?? services?.GetService<IOptions<McpGatewayOptions>>()?.Value.MaximumToolSchemaBytes
            ?? McpGatewayOptions.DefaultMaximumToolSchemaBytes;
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var bytes = Encoding.UTF8.GetByteCount(McpGatewayJsonSerializer.TrySerializeToElement(result)!.Value.GetRawText());
        if (bytes > limit)
        { throw new McpGatewaySchemaTooLargeException(bytes, limit, result.SchemaHash, result.Revision); }
        return result;
    }

    public async Task<McpGatewayDiscoveryResult> DiscoverAsync(McpGatewayDiscoveryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.MaxBytes, McpGatewayDiscoveryRequest.MinimumMaxBytes);
        var search = await gateway.SearchAsync(new McpGatewaySearchRequest(request.Query, request.MaxResults)
        { SourceId = request.SourceId, Category = request.Category }, cancellationToken);
        IReadOnlyDictionary<string, McpGatewayToolDescriptor> definitions = search.Matches.Concat(search.RelatedMatches).Concat(search.NextStepMatches)
            .Any(match => match.SchemaHash is null || match.DefinitionHash is null)
            ? (await gateway.ListToolsAsync(cancellationToken)).ToDictionary(tool => tool.ToolId, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, McpGatewayToolDescriptor>();
        var matches = new List<McpGatewayToolSummary>();
        Append(matches, search.Matches, PrimaryRole, request, definitions);
        Append(matches, search.RelatedMatches, RelatedRole, request, definitions);
        Append(matches, search.NextStepMatches, NextStepRole, request, definitions);
        var revision = Hash(matches);
        var identity = Hash(new { request.Query, request.MaxResults, request.SourceId, request.Category, revision });
        var offset = ReadOffset(request.ContinuationToken, identity, matches.Count);
        var page = new List<McpGatewayToolSummary>();
        var result = Measure(new(page, revision, null, 0));
        for (var index = offset; index < matches.Count; index++)
        {
            page.Add(matches[index]);
            var token = index + 1 < matches.Count ? CreateToken(identity, index + 1) : null;
            var candidate = Measure(new(page.ToArray(), revision, token, 0));
            if (candidate.SerializedBytes > request.MaxBytes)
            {
                page.RemoveAt(page.Count - 1);
                if (page.Count == 0)
                {
                    throw new InvalidOperationException("The discovery budget cannot fit a complete tool summary.");
                }
                return Measure(new(page.ToArray(), revision, CreateToken(identity, index), 0));
            }
            result = candidate;
        }
        return result;
    }

    private static void Append(List<McpGatewayToolSummary> target, IReadOnlyList<McpGatewaySearchMatch> matches, string role,
        McpGatewayDiscoveryRequest request, IReadOnlyDictionary<string, McpGatewayToolDescriptor> definitions)
    {
        foreach (var match in matches)
        {
            if (request.SourceId is { } source && !source.Equals(match.SourceId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (request.Category is { } category && !match.Categories.Contains(category, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }
            definitions.TryGetValue(match.ToolId, out var definition);
            if (definition is null && (match.SchemaHash is null || match.DefinitionHash is null))
            { continue; }
            var length = Math.Min(match.Description.Length, PurposeLength);
            if (length < match.Description.Length && char.IsHighSurrogate(match.Description[length - 1]))
            {
                length--;
            }
            var purpose = match.Description[..length];
            target.Add(new(match.ToolId, match.SourceId, purpose, match.Score, match.SchemaHash ?? SchemaHash(definition!.InputSchema, definition.OutputSchema),
                match.DefinitionHash ?? definition!.DefinitionHash ?? McpGatewayDefinitionHash.Create(definition, definition.BindingFingerprint ?? string.Empty).DefinitionHash)
            { IsReadOnly = match.IsReadOnly, IsDestructive = match.IsDestructive, Categories = match.Categories, Role = role });
        }
    }

    internal static string Hash(object? value)
    {
        var element = McpGatewayJsonSerializer.TrySerializeToElement(value);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(element?.GetRawText() ?? "null")));
    }

    private static string SchemaHash(System.Text.Json.JsonElement InputSchema, System.Text.Json.JsonElement? OutputSchema) =>
        McpGatewayDefinitionHash.Compute(new { InputSchema, OutputSchema });

    private static McpGatewayDiscoveryResult Measure(McpGatewayDiscoveryResult value)
    {
        var previous = -1;
        while (previous != value.SerializedBytes)
        {
            previous = value.SerializedBytes;
            var json = McpGatewayJsonSerializer.TrySerializeToElement(value) ?? throw new InvalidOperationException("Discovery serialization failed.");
            value = value with { SerializedBytes = Encoding.UTF8.GetByteCount(json.GetRawText()) };
        }
        return value;
    }

    private static string CreateToken(string identity, int offset) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{identity}:{offset.ToString(CultureInfo.InvariantCulture)}"));

    private static int ReadOffset(string? token, string identity, int count)
    {
        if (token is null)
        {
            return 0;
        }
        if (token.Length > MaximumContinuationLength)
        {
            throw new ArgumentException("Invalid discovery continuation token.", nameof(token));
        }
        string text;
        try
        { text = Encoding.UTF8.GetString(Convert.FromBase64String(token)); }
        catch (FormatException exception) { throw new ArgumentException("Invalid discovery continuation token.", nameof(token), exception); }
        var parts = text.Split(':');
        if (parts.Length != 2 || !parts[0].Equals(identity, StringComparison.Ordinal)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var offset)
            || offset < 0 || offset >= count)
        {
            throw new ArgumentException("The discovery continuation is invalid or the catalog changed.", nameof(token));
        }
        return offset;
    }
}
