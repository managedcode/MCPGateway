using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway;

public sealed class McpGateway(
    IServiceProvider serviceProvider,
    IOptions<McpGatewayOptions> options,
    ILogger<McpGateway> logger,
    ILoggerFactory loggerFactory)
    : IMcpGateway, IMcpGatewayRegistry
{
    private const string DefaultSourceId = "local";
    private const string QueryArgumentName = "query";
    private const string ContextArgumentName = "context";
    private const string ContextSummaryArgumentName = "contextSummary";
    private const string GatewayInvocationMetaKey = "managedCodeMcpGateway";
    private const string SearchModeEmpty = "empty";
    private const string SearchModeBrowse = "browse";
    private const string SearchModeLexical = "lexical";
    private const string SearchModeVector = "vector";
    private const string SourceLoadFailedDiagnosticCode = "source_load_failed";
    private const string DuplicateToolIdDiagnosticCode = "duplicate_tool_id";
    private const string EmbeddingCountMismatchDiagnosticCode = "embedding_count_mismatch";
    private const string EmbeddingGeneratorMissingDiagnosticCode = "embedding_generator_missing";
    private const string EmbeddingFailedDiagnosticCode = "embedding_failed";
    private const string QueryVectorEmptyDiagnosticCode = "query_vector_empty";
    private const string LexicalFallbackDiagnosticCode = "lexical_fallback";
    private const string VectorSearchFailedDiagnosticCode = "vector_search_failed";
    private const string CommandRequiredMessage = "A command is required.";
    private const string SourceLoadFailedMessageTemplate = "Failed to load tools from source '{0}': {1}";
    private const string DuplicateToolIdMessageTemplate = "Skipped duplicate tool id '{0}'.";
    private const string EmbeddingCountMismatchMessageTemplate = "Embedding generation returned {0} vectors for {1} tools.";
    private const string EmbeddingGeneratorMissingMessage = "No keyed or unkeyed IEmbeddingGenerator<string, Embedding<float>> is registered. Lexical fallback only.";
    private const string EmbeddingFailedMessageTemplate = "Embedding generation failed: {0}";
    private const string QueryVectorEmptyMessage = "Embedding generator returned an empty query vector.";
    private const string LexicalFallbackMessage = "Vector search is unavailable. Lexical ranking was used.";
    private const string VectorSearchFailedMessageTemplate = "Vector ranking failed and lexical fallback was used: {0}";
    private const string ToolNotInvokableMessageTemplate = "Tool '{0}' is not invokable.";
    private const string ToolIdOrToolNameRequiredMessage = "Either ToolId or ToolName is required.";
    private const string ToolIdNotFoundMessageTemplate = "Tool '{0}' was not found.";
    private const string ToolNameAmbiguousMessageTemplate = "Tool '{0}' is ambiguous. Use ToolId or specify SourceId explicitly.";
    private const string FailedToLoadGatewaySourceLogMessage = "Failed to load gateway source {SourceId}.";
    private const string EmbeddingGenerationFailedLogMessage = "Gateway embedding generation failed. Falling back to lexical search.";
    private const string GatewayIndexRebuiltLogMessage = "Gateway index rebuilt. Tools={ToolCount} VectorizedTools={VectorizedToolCount}.";
    private const string GatewayVectorSearchFailedLogMessage = "Gateway vector search failed. Falling back to lexical ranking.";
    private const string GatewayInvocationFailedLogMessage = "Gateway invocation failed for {ToolId}.";
    private const string InputSchemaPropertiesPropertyName = "properties";
    private const string InputSchemaRequiredPropertyName = "required";
    private const string InputSchemaDescriptionPropertyName = "description";
    private const string InputSchemaTypePropertyName = "type";
    private const string InputSchemaEnumPropertyName = "enum";
    private const string DisplayNamePropertyName = "DisplayName";
    private const string ToolNameLabel = "Tool name: ";
    private const string DisplayNameLabel = "Display name: ";
    private const string DescriptionLabel = "Description: ";
    private const string RequiredArgumentsLabel = "Required arguments: ";
    private const string ParameterLabel = "Parameter ";
    private const string TypeLabel = "Type ";
    private const string TypicalValuesLabel = "Typical values: ";
    private const string InputSchemaLabel = "Input schema: ";
    private const string ContextSummaryPrefix = "context summary: ";
    private const string ContextPrefix = "context: ";
    private const string PluralSuffixIes = "ies";
    private const string PluralSuffixEs = "es";

    private static readonly char[] TokenSeparators =
    [
        ' ',
        '\t',
        '\r',
        '\n',
        '_',
        '-',
        '.',
        ',',
        ';',
        ':',
        '/',
        '\\',
        '(',
        ')',
        '[',
        ']',
        '{',
        '}',
        '"',
        '\'',
        '?',
        '!'
    ];
    private static readonly CompositeFormat SourceLoadFailedMessageFormat = CompositeFormat.Parse(SourceLoadFailedMessageTemplate);
    private static readonly CompositeFormat DuplicateToolIdMessageFormat = CompositeFormat.Parse(DuplicateToolIdMessageTemplate);
    private static readonly CompositeFormat EmbeddingCountMismatchMessageFormat = CompositeFormat.Parse(EmbeddingCountMismatchMessageTemplate);
    private static readonly CompositeFormat EmbeddingFailedMessageFormat = CompositeFormat.Parse(EmbeddingFailedMessageTemplate);
    private static readonly CompositeFormat VectorSearchFailedMessageFormat = CompositeFormat.Parse(VectorSearchFailedMessageTemplate);
    private static readonly CompositeFormat ToolNotInvokableMessageFormat = CompositeFormat.Parse(ToolNotInvokableMessageTemplate);
    private static readonly CompositeFormat ToolIdNotFoundMessageFormat = CompositeFormat.Parse(ToolIdNotFoundMessageTemplate);
    private static readonly CompositeFormat ToolNameAmbiguousMessageFormat = CompositeFormat.Parse(ToolNameAmbiguousMessageTemplate);

    private readonly object _gate = new();
    private readonly SemaphoreSlim _rebuildLock = new(1, 1);
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly ILogger<McpGateway> _logger = logger;
    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    private readonly List<McpGatewayToolSourceRegistration> _registrations = options.Value.SourceRegistrations.ToList();
    private readonly int _defaultSearchLimit = Math.Max(1, options.Value.DefaultSearchLimit);
    private readonly int _maxSearchResults = Math.Max(1, options.Value.MaxSearchResults);
    private readonly int _maxDescriptorLength = Math.Max(256, options.Value.MaxDescriptorLength);
    private ToolCatalogSnapshot? _snapshot;
    private bool _disposed;

    public void AddTool(string sourceId, AITool tool, string? displayName = null)
        => AddTool(tool, sourceId, displayName);

    public void AddTool(AITool tool, string sourceId = DefaultSourceId, string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(tool);

        lock (_gate)
        {
            ThrowIfDisposed();

            var existing = _registrations
                .OfType<McpGatewayLocalToolSourceRegistration>()
                .FirstOrDefault(item => string.Equals(item.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                existing = new McpGatewayLocalToolSourceRegistration(sourceId.Trim(), displayName);
                _registrations.Add(existing);
            }

            existing.AddTool(tool);
            _snapshot = null;
        }
    }

    public void AddTools(string sourceId, IEnumerable<AITool> tools, string? displayName = null)
        => AddTools(tools, sourceId, displayName);

    public void AddTools(IEnumerable<AITool> tools, string sourceId = DefaultSourceId, string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(tools);

        foreach (var tool in tools)
        {
            AddTool(tool, sourceId, displayName);
        }
    }

    public void AddHttpServer(
        string sourceId,
        Uri endpoint,
        IReadOnlyDictionary<string, string>? headers = null,
        string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        AddRegistration(new McpGatewayHttpToolSourceRegistration(sourceId.Trim(), endpoint, headers, displayName));
    }

    public void AddStdioServer(
        string sourceId,
        string command,
        IReadOnlyList<string>? arguments = null,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string?>? environmentVariables = null,
        string? displayName = null)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException(CommandRequiredMessage, nameof(command));
        }

        AddRegistration(new McpGatewayStdioToolSourceRegistration(
            sourceId.Trim(),
            command.Trim(),
            arguments,
            workingDirectory,
            environmentVariables,
            displayName));
    }

    public void AddMcpClient(
        string sourceId,
        McpClient client,
        bool disposeClient = false,
        string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        AddRegistration(new McpGatewayProvidedClientToolSourceRegistration(
            sourceId.Trim(),
            _ => ValueTask.FromResult(client),
            disposeClient,
            displayName));
    }

    public void AddMcpClientFactory(
        string sourceId,
        Func<CancellationToken, ValueTask<McpClient>> clientFactory,
        bool disposeClient = true,
        string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(clientFactory);
        AddRegistration(new McpGatewayProvidedClientToolSourceRegistration(
            sourceId.Trim(),
            clientFactory,
            disposeClient,
            displayName));
    }

    public async Task<McpGatewayIndexBuildResult> BuildIndexAsync(CancellationToken cancellationToken = default)
    {
        await _rebuildLock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();

            var registrations = CopyRegistrations();
            var diagnostics = new List<McpGatewayDiagnostic>();
            var entries = new List<ToolCatalogEntry>();
            var seenToolIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var registration in registrations)
            {
                cancellationToken.ThrowIfCancellationRequested();

                IReadOnlyList<AITool> tools;
                try
                {
                    tools = await registration.LoadToolsAsync(_loggerFactory, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    diagnostics.Add(new McpGatewayDiagnostic(
                        SourceLoadFailedDiagnosticCode,
                        string.Format(
                            CultureInfo.InvariantCulture,
                            SourceLoadFailedMessageFormat,
                            registration.SourceId,
                            ex.GetBaseException().Message)));
                    _logger.LogWarning(ex, FailedToLoadGatewaySourceLogMessage, registration.SourceId);
                    continue;
                }

                foreach (var tool in tools)
                {
                    var descriptor = BuildDescriptor(registration, tool);
                    if (descriptor is null)
                    {
                        continue;
                    }

                    if (!seenToolIds.Add(descriptor.ToolId))
                    {
                        diagnostics.Add(new McpGatewayDiagnostic(
                            DuplicateToolIdDiagnosticCode,
                            string.Format(CultureInfo.InvariantCulture, DuplicateToolIdMessageFormat, descriptor.ToolId)));
                        continue;
                    }

                    entries.Add(new ToolCatalogEntry(
                        descriptor,
                        tool,
                        BuildDescriptorDocument(descriptor, tool)));
                }
            }

            var vectorizedToolCount = 0;
            if (entries.Count > 0)
            {
                try
                {
                    await using var embeddingGeneratorLease = ResolveEmbeddingGenerator();
                    if (embeddingGeneratorLease.Generator is IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator)
                    {
                        var embeddings = (await embeddingGenerator.GenerateAsync(
                                entries.Select(static item => item.Document),
                                cancellationToken: cancellationToken))
                            .ToList();
                        if (embeddings.Count == entries.Count)
                        {
                            for (var index = 0; index < entries.Count; index++)
                            {
                                var vector = embeddings[index].Vector.ToArray();
                                if (vector.Length == 0)
                                {
                                    continue;
                                }

                                entries[index] = entries[index] with
                                {
                                    Vector = vector,
                                    Magnitude = CalculateMagnitude(vector)
                                };

                                if (entries[index].Magnitude > double.Epsilon)
                                {
                                    vectorizedToolCount++;
                                }
                            }
                        }
                        else
                        {
                            diagnostics.Add(new McpGatewayDiagnostic(
                                EmbeddingCountMismatchDiagnosticCode,
                                string.Format(
                                    CultureInfo.InvariantCulture,
                                    EmbeddingCountMismatchMessageFormat,
                                    embeddings.Count,
                                    entries.Count)));
                        }
                    }
                    else
                    {
                        diagnostics.Add(new McpGatewayDiagnostic(
                            EmbeddingGeneratorMissingDiagnosticCode,
                            EmbeddingGeneratorMissingMessage));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    diagnostics.Add(new McpGatewayDiagnostic(
                        EmbeddingFailedDiagnosticCode,
                        string.Format(CultureInfo.InvariantCulture, EmbeddingFailedMessageFormat, ex.GetBaseException().Message)));
                    _logger.LogWarning(ex, EmbeddingGenerationFailedLogMessage);
                }
            }

            var snapshot = new ToolCatalogSnapshot(
                entries
                    .OrderBy(static item => item.Descriptor.ToolName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static item => item.Descriptor.SourceId, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                vectorizedToolCount > 0);

            lock (_gate)
            {
                _snapshot = snapshot;
            }

            _logger.LogInformation(
                GatewayIndexRebuiltLogMessage,
                snapshot.Entries.Count,
                vectorizedToolCount);

            return new McpGatewayIndexBuildResult(
                snapshot.Entries.Count,
                vectorizedToolCount,
                snapshot.HasVectors,
                diagnostics);
        }
        finally
        {
            _rebuildLock.Release();
        }
    }

    public async Task<IReadOnlyList<McpGatewayToolDescriptor>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await GetSnapshotAsync(cancellationToken);
        return snapshot.Entries
            .Select(static item => item.Descriptor)
            .ToList();
    }

    public async Task<McpGatewaySearchResult> SearchAsync(
        string? query,
        int? maxResults = null,
        CancellationToken cancellationToken = default)
        => await SearchAsync(
            new McpGatewaySearchRequest(
                Query: query,
                MaxResults: maxResults),
            cancellationToken);

    public async Task<McpGatewaySearchResult> SearchAsync(
        McpGatewaySearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var snapshot = await GetSnapshotAsync(cancellationToken);
        var limit = Math.Clamp(request.MaxResults.GetValueOrDefault(_defaultSearchLimit), 1, _maxSearchResults);
        var diagnostics = new List<McpGatewayDiagnostic>();

        if (snapshot.Entries.Count == 0)
        {
            return new McpGatewaySearchResult([], diagnostics, SearchModeEmpty);
        }

        var rawQuery = request.Query?.Trim();
        var effectiveQuery = BuildEffectiveSearchQuery(request);
        if (string.IsNullOrWhiteSpace(effectiveQuery))
        {
            var browse = snapshot.Entries
                .Take(limit)
                .Select(static entry => ToSearchMatch(entry, 0d))
                .ToList();
            return new McpGatewaySearchResult(browse, diagnostics, SearchModeBrowse);
        }

        IReadOnlyList<ScoredToolEntry> ranked;
        var rankingMode = SearchModeLexical;
        if (snapshot.HasVectors)
        {
            try
            {
                await using var embeddingGeneratorLease = ResolveEmbeddingGenerator();
                if (embeddingGeneratorLease.Generator is IEmbeddingGenerator<string, Embedding<float>> generator)
                {
                    var embedding = await generator.GenerateAsync(effectiveQuery, cancellationToken: cancellationToken);
                    var queryVector = embedding.Vector.ToArray();
                    var queryMagnitude = CalculateMagnitude(queryVector);
                    if (queryMagnitude > double.Epsilon)
                    {
                        ranked = snapshot.Entries
                            .Select(entry => new ScoredToolEntry(
                                entry,
                                ApplySearchBoosts(
                                    entry,
                                    rawQuery ?? effectiveQuery,
                                    CalculateCosine(entry, queryVector, queryMagnitude))))
                            .OrderByDescending(static item => item.Score)
                            .ThenBy(static item => item.Entry.Descriptor.ToolName, StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        rankingMode = SearchModeVector;
                    }
                    else
                    {
                        ranked = RankLexically(snapshot.Entries, effectiveQuery);
                        diagnostics.Add(new McpGatewayDiagnostic(QueryVectorEmptyDiagnosticCode, QueryVectorEmptyMessage));
                    }
                }
                else
                {
                    ranked = RankLexically(snapshot.Entries, effectiveQuery);
                    diagnostics.Add(new McpGatewayDiagnostic(
                        LexicalFallbackDiagnosticCode,
                        LexicalFallbackMessage));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ranked = RankLexically(snapshot.Entries, effectiveQuery);
                diagnostics.Add(new McpGatewayDiagnostic(
                    VectorSearchFailedDiagnosticCode,
                    string.Format(CultureInfo.InvariantCulture, VectorSearchFailedMessageFormat, ex.GetBaseException().Message)));
                _logger.LogWarning(ex, GatewayVectorSearchFailedLogMessage);
            }
        }
        else
        {
            ranked = RankLexically(snapshot.Entries, effectiveQuery);
            diagnostics.Add(new McpGatewayDiagnostic(
                LexicalFallbackDiagnosticCode,
                LexicalFallbackMessage));
        }

        var matches = ranked
            .Take(limit)
            .Select(item => ToSearchMatch(item.Entry, item.Score))
            .ToList();

        return new McpGatewaySearchResult(matches, diagnostics, rankingMode);
    }

    private EmbeddingGeneratorLease ResolveEmbeddingGenerator()
    {
        if (_serviceProvider.GetService(typeof(IServiceScopeFactory)) is not IServiceScopeFactory scopeFactory)
        {
            return new EmbeddingGeneratorLease(ResolveEmbeddingGenerator(_serviceProvider));
        }

        var scope = scopeFactory.CreateAsyncScope();
        var generator = ResolveEmbeddingGenerator(scope.ServiceProvider);
        return new EmbeddingGeneratorLease(generator, scope);
    }

    private static IEmbeddingGenerator<string, Embedding<float>>? ResolveEmbeddingGenerator(IServiceProvider serviceProvider)
        => serviceProvider.GetKeyedService<IEmbeddingGenerator<string, Embedding<float>>>(McpGatewayServiceKeys.EmbeddingGenerator)
            ?? serviceProvider.GetService<IEmbeddingGenerator<string, Embedding<float>>>();

    public async Task<McpGatewayInvokeResult> InvokeAsync(
        McpGatewayInvokeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var snapshot = await GetSnapshotAsync(cancellationToken);
        var resolution = ResolveInvocationTarget(snapshot, request);
        if (!resolution.IsSuccess || resolution.Entry is null)
        {
            return new McpGatewayInvokeResult(
                false,
                request.ToolId ?? string.Empty,
                request.SourceId ?? string.Empty,
                request.ToolName ?? string.Empty,
                Output: null,
                Error: resolution.Error);
        }

        var entry = resolution.Entry;
        var arguments = request.Arguments is { Count: > 0 }
            ? new Dictionary<string, object?>(request.Arguments, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(request.Query) &&
            !arguments.ContainsKey(QueryArgumentName) &&
            SupportsArgument(entry.Descriptor, QueryArgumentName))
        {
            arguments[QueryArgumentName] = request.Query;
        }

        MapRequestArgument(arguments, entry.Descriptor, ContextArgumentName, request.Context);
        MapRequestArgument(arguments, entry.Descriptor, ContextSummaryArgumentName, request.ContextSummary);

        try
        {
            var resolvedMcpTool = entry.Tool as McpClientTool ?? entry.Tool.GetService<McpClientTool>();
            if (resolvedMcpTool is not null)
            {
                var result = await AttachInvocationMeta(resolvedMcpTool, request).CallAsync(
                    arguments,
                    progress: null,
                    options: new RequestOptions(),
                    cancellationToken: cancellationToken);

                return new McpGatewayInvokeResult(
                    true,
                    entry.Descriptor.ToolId,
                    entry.Descriptor.SourceId,
                    entry.Descriptor.ToolName,
                    ExtractMcpOutput(result));
            }

            var function = entry.Tool as AIFunction ?? entry.Tool.GetService<AIFunction>();
            if (function is null)
            {
                return new McpGatewayInvokeResult(
                    false,
                    entry.Descriptor.ToolId,
                    entry.Descriptor.SourceId,
                    entry.Descriptor.ToolName,
                    Output: null,
                    Error: string.Format(CultureInfo.InvariantCulture, ToolNotInvokableMessageFormat, entry.Descriptor.ToolName));
            }

            var resultValue = await function.InvokeAsync(
                new AIFunctionArguments(arguments, StringComparer.OrdinalIgnoreCase),
                cancellationToken);
            return new McpGatewayInvokeResult(
                true,
                entry.Descriptor.ToolId,
                entry.Descriptor.SourceId,
                entry.Descriptor.ToolName,
                NormalizeFunctionOutput(resultValue));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, GatewayInvocationFailedLogMessage, entry.Descriptor.ToolId);
            return new McpGatewayInvokeResult(
                false,
                entry.Descriptor.ToolId,
                entry.Descriptor.SourceId,
                entry.Descriptor.ToolName,
                Output: null,
                Error: ex.GetBaseException().Message);
        }
    }

    public IReadOnlyList<AITool> CreateMetaTools(
        string searchToolName = McpGatewayToolSet.DefaultSearchToolName,
        string invokeToolName = McpGatewayToolSet.DefaultInvokeToolName)
        => new McpGatewayToolSet(this).CreateTools(searchToolName, invokeToolName);

    public async ValueTask DisposeAsync()
    {
        List<McpGatewayToolSourceRegistration> registrations;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            registrations = _registrations.ToList();
            _registrations.Clear();
            _snapshot = null;
        }

        foreach (var registration in registrations)
        {
            await registration.DisposeAsync();
        }

        _rebuildLock.Dispose();
    }

    private void AddRegistration(McpGatewayToolSourceRegistration registration)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _registrations.Add(registration);
            _snapshot = null;
        }
    }

    private List<McpGatewayToolSourceRegistration> CopyRegistrations()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return _registrations.ToList();
        }
    }

    private async Task<ToolCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        ToolCatalogSnapshot? snapshot;
        lock (_gate)
        {
            snapshot = _snapshot;
        }

        if (snapshot is not null)
        {
            return snapshot;
        }

        await BuildIndexAsync(cancellationToken);

        lock (_gate)
        {
            return _snapshot ?? ToolCatalogSnapshot.Empty;
        }
    }

    private static McpGatewayToolDescriptor? BuildDescriptor(
        McpGatewayToolSourceRegistration registration,
        AITool tool)
    {
        if (string.IsNullOrWhiteSpace(tool.Name))
        {
            return null;
        }

        var toolName = tool.Name.Trim();
        var sourceKind = registration.Kind switch
        {
            McpGatewaySourceRegistrationKind.Http => McpGatewaySourceKind.HttpMcp,
            McpGatewaySourceRegistrationKind.Stdio => McpGatewaySourceKind.StdioMcp,
            McpGatewaySourceRegistrationKind.CustomMcpClient => McpGatewaySourceKind.CustomMcpClient,
            _ => McpGatewaySourceKind.Local
        };

        var inputSchemaJson = ResolveInputSchemaJson(tool);
        var requiredArguments = ExtractRequiredArguments(inputSchemaJson);

        return new McpGatewayToolDescriptor(
            ToolId: $"{registration.SourceId}:{toolName}",
            SourceId: registration.SourceId,
            SourceKind: sourceKind,
            ToolName: toolName,
            DisplayName: ResolveDisplayName(tool),
            Description: tool.Description ?? string.Empty,
            RequiredArguments: requiredArguments,
            InputSchemaJson: inputSchemaJson);
    }

    private string BuildDescriptorDocument(McpGatewayToolDescriptor descriptor, AITool tool)
    {
        var builder = new StringBuilder();
        builder.Append(ToolNameLabel);
        builder.AppendLine(descriptor.ToolName);

        if (!string.IsNullOrWhiteSpace(descriptor.DisplayName))
        {
            builder.Append(DisplayNameLabel);
            builder.AppendLine(descriptor.DisplayName);
        }

        if (!string.IsNullOrWhiteSpace(descriptor.Description))
        {
            builder.Append(DescriptionLabel);
            builder.AppendLine(descriptor.Description);
        }

        if (descriptor.RequiredArguments.Count > 0)
        {
            builder.Append(RequiredArgumentsLabel);
            builder.AppendLine(string.Join(", ", descriptor.RequiredArguments));
        }

        AppendInputSchema(builder, descriptor.InputSchemaJson);
        var document = builder.ToString().Trim();
        return document.Length <= _maxDescriptorLength
            ? document
            : document[.._maxDescriptorLength];
    }

    private static void AppendInputSchema(StringBuilder builder, string? inputSchemaJson)
    {
        if (string.IsNullOrWhiteSpace(inputSchemaJson))
        {
            return;
        }

        try
        {
            using var schemaDocument = JsonDocument.Parse(inputSchemaJson);
            if (!schemaDocument.RootElement.TryGetProperty(InputSchemaPropertiesPropertyName, out var properties) ||
                properties.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var property in properties.EnumerateObject())
            {
                builder.Append(ParameterLabel);
                builder.Append(property.Name);
                builder.Append(": ");

                if (property.Value.TryGetProperty(InputSchemaDescriptionPropertyName, out var description) &&
                    description.ValueKind == JsonValueKind.String)
                {
                    builder.Append(description.GetString());
                    builder.Append(". ");
                }

                if (property.Value.TryGetProperty(InputSchemaTypePropertyName, out var type) &&
                    type.ValueKind == JsonValueKind.String)
                {
                    builder.Append(TypeLabel);
                    builder.Append(type.GetString());
                    builder.Append(". ");
                }

                if (property.Value.TryGetProperty(InputSchemaEnumPropertyName, out var enumValues) &&
                    enumValues.ValueKind == JsonValueKind.Array)
                {
                    var values = enumValues
                        .EnumerateArray()
                        .Where(static item => item.ValueKind == JsonValueKind.String)
                        .Select(static item => item.GetString())
                        .Where(static value => !string.IsNullOrWhiteSpace(value))
                        .Select(static value => value!)
                        .ToList();
                    if (values.Count > 0)
                    {
                        builder.Append(TypicalValuesLabel);
                        builder.Append(string.Join(", ", values));
                        builder.Append(". ");
                    }
                }

                builder.AppendLine();
            }
        }
        catch (JsonException)
        {
            builder.Append(InputSchemaLabel);
            builder.AppendLine(inputSchemaJson);
        }
    }

    private static string? ResolveDisplayName(AITool tool)
    {
        if (tool is McpClientTool mcpTool)
        {
            return mcpTool.ProtocolTool?.Title;
        }

        var function = tool as AIFunction ?? tool.GetService<AIFunction>();
        if (function?.AdditionalProperties is { Count: > 0 } &&
            function.AdditionalProperties.TryGetValue(DisplayNamePropertyName, out var displayName) &&
            displayName is string value &&
            !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return null;
    }

    private static string? ResolveInputSchemaJson(AITool tool)
    {
        if (tool is McpClientTool mcpTool)
        {
            return SerializeSchema(mcpTool.ProtocolTool?.InputSchema);
        }

        var function = tool as AIFunction ?? tool.GetService<AIFunction>();
        if (function is null)
        {
            return null;
        }

        return function.JsonSchema.ValueKind == JsonValueKind.Undefined
            ? null
            : function.JsonSchema.GetRawText();
    }

    private static string? SerializeSchema(object? schema)
    {
        return schema switch
        {
            null => null,
            JsonElement element when element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonElement element => element.GetRawText(),
            JsonNode node => node.ToJsonString(),
            _ => JsonSerializer.Serialize(schema)
        };
    }

    private static IReadOnlyList<string> ExtractRequiredArguments(string? inputSchemaJson)
    {
        if (string.IsNullOrWhiteSpace(inputSchemaJson))
        {
            return [];
        }

        try
        {
            using var schemaDocument = JsonDocument.Parse(inputSchemaJson);
            if (!schemaDocument.RootElement.TryGetProperty(InputSchemaRequiredPropertyName, out var required) ||
                required.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return required
                .EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.String)
                .Select(static item => item.GetString())
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static double CalculateCosine(ToolCatalogEntry entry, float[] queryVector, double queryMagnitude)
    {
        if (entry.Vector is null || entry.Magnitude <= double.Epsilon || queryMagnitude <= double.Epsilon)
        {
            return 0d;
        }

        var overlap = Math.Min(entry.Vector.Length, queryVector.Length);
        if (overlap == 0)
        {
            return 0d;
        }

        var dot = 0d;
        for (var index = 0; index < overlap; index++)
        {
            dot += entry.Vector[index] * queryVector[index];
        }

        return dot / (entry.Magnitude * queryMagnitude);
    }

    private static double CalculateMagnitude(IReadOnlyList<float> vector)
    {
        if (vector.Count == 0)
        {
            return 0d;
        }

        var magnitudeSquared = 0d;
        foreach (var component in vector)
        {
            magnitudeSquared += component * component;
        }

        return Math.Sqrt(magnitudeSquared);
    }

    private static string BuildEffectiveSearchQuery(McpGatewaySearchRequest request)
    {
        List<string> parts = [];

        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            parts.Add(request.Query.Trim());
        }

        if (!string.IsNullOrWhiteSpace(request.ContextSummary))
        {
            parts.Add(string.Concat(ContextSummaryPrefix, request.ContextSummary.Trim()));
        }

        var flattenedContext = FlattenContext(request.Context);
        if (!string.IsNullOrWhiteSpace(flattenedContext))
        {
            parts.Add(string.Concat(ContextPrefix, flattenedContext));
        }

        return string.Join(" | ", parts);
    }

    private static string? FlattenContext(IReadOnlyDictionary<string, object?>? context)
    {
        if (context is not { Count: > 0 })
        {
            return null;
        }

        var terms = new List<string>();
        foreach (var (key, value) in context)
        {
            AppendContextTerms(terms, key, value);
        }

        return terms.Count == 0
            ? null
            : string.Join("; ", terms);
    }

    private static void AppendContextTerms(List<string> terms, string key, object? value)
    {
        if (value is null)
        {
            return;
        }

        switch (value)
        {
            case string text when !string.IsNullOrWhiteSpace(text):
                terms.Add(FormattableString.Invariant($"{key} {text.Trim()}"));
                return;

            case JsonElement element:
                AppendJsonElementTerms(terms, key, element);
                return;

            case JsonNode node:
                if (node is not null)
                {
                    AppendJsonElementTerms(terms, key, JsonSerializer.SerializeToElement(node));
                }
                return;

            case IReadOnlyDictionary<string, object?> dictionary:
                foreach (var (childKey, childValue) in dictionary)
                {
                    AppendContextTerms(terms, $"{key} {childKey}", childValue);
                }
                return;

            case IEnumerable<KeyValuePair<string, object?>> dictionaryEntries:
                foreach (var (childKey, childValue) in dictionaryEntries)
                {
                    AppendContextTerms(terms, $"{key} {childKey}", childValue);
                }
                return;

            case System.Collections.IDictionary legacyDictionary:
                foreach (System.Collections.DictionaryEntry entry in legacyDictionary)
                {
                    var childKey = Convert.ToString(entry.Key, CultureInfo.InvariantCulture);
                    if (!string.IsNullOrWhiteSpace(childKey))
                    {
                        AppendContextTerms(terms, $"{key} {childKey}", entry.Value);
                    }
                }
                return;

            case System.Collections.IEnumerable enumerable when value is not string:
                foreach (var item in enumerable)
                {
                    AppendContextTerms(terms, key, item);
                }
                return;

            default:
                var scalar = Convert.ToString(value, CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(scalar))
                {
                    terms.Add(FormattableString.Invariant($"{key} {scalar}"));
                }
                return;
        }
    }

    private static void AppendJsonElementTerms(List<string> terms, string key, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    AppendJsonElementTerms(terms, $"{key} {property.Name}", property.Value);
                }
                return;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    AppendJsonElementTerms(terms, key, item);
                }
                return;

            case JsonValueKind.String:
                var text = element.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    terms.Add(FormattableString.Invariant($"{key} {text.Trim()}"));
                }
                return;

            case JsonValueKind.True:
            case JsonValueKind.False:
                terms.Add(FormattableString.Invariant($"{key} {element.GetBoolean()}"));
                return;

            case JsonValueKind.Number:
                terms.Add(FormattableString.Invariant($"{key} {element}"));
                return;

            default:
                return;
        }
    }

    private static void MapRequestArgument(
        IDictionary<string, object?> arguments,
        McpGatewayToolDescriptor descriptor,
        string argumentName,
        object? value)
    {
        if (value is null ||
            arguments.ContainsKey(argumentName) ||
            !SupportsArgument(descriptor, argumentName))
        {
            return;
        }

        if (value is string text && string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        arguments[argumentName] = value;
    }

    private static bool SupportsArgument(
        McpGatewayToolDescriptor descriptor,
        string argumentName)
    {
        if (descriptor.RequiredArguments.Contains(argumentName, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(descriptor.InputSchemaJson))
        {
            return false;
        }

        try
        {
            using var schemaDocument = JsonDocument.Parse(descriptor.InputSchemaJson);
            if (!schemaDocument.RootElement.TryGetProperty(InputSchemaPropertiesPropertyName, out var properties) ||
                properties.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            return properties
                .EnumerateObject()
                .Any(property => string.Equals(property.Name, argumentName, StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static McpClientTool AttachInvocationMeta(McpClientTool tool, McpGatewayInvokeRequest request)
    {
        var meta = BuildInvocationMeta(request);
        return meta is null ? tool : tool.WithMeta(meta);
    }

    private static JsonObject? BuildInvocationMeta(McpGatewayInvokeRequest request)
    {
        var payload = new JsonObject();
        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            payload[QueryArgumentName] = request.Query.Trim();
        }

        if (!string.IsNullOrWhiteSpace(request.ContextSummary))
        {
            payload[ContextSummaryArgumentName] = request.ContextSummary.Trim();
        }

        if (request.Context is { Count: > 0 })
        {
            var contextNode = JsonSerializer.SerializeToNode(request.Context);
            if (contextNode is not null)
            {
                payload[ContextArgumentName] = contextNode;
            }
        }

        return payload.Count == 0
            ? null
            : new JsonObject
            {
                [GatewayInvocationMetaKey] = payload
            };
    }

    private static double ApplySearchBoosts(ToolCatalogEntry entry, string query, double score)
    {
        if (string.Equals(entry.Descriptor.ToolName, query, StringComparison.OrdinalIgnoreCase))
        {
            score += 0.1d;
        }
        else if (entry.Descriptor.ToolName.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            score += 0.03d;
        }

        return Math.Clamp(score, 0d, 1d);
    }

    private static IReadOnlyList<ScoredToolEntry> RankLexically(
        IReadOnlyList<ToolCatalogEntry> entries,
        string query)
    {
        var searchTerms = BuildSearchTerms(query);
        return entries
            .Select(entry => new ScoredToolEntry(entry, CalculateLexicalScore(entry, query, searchTerms)))
            .OrderByDescending(static item => item.Score)
            .ThenBy(static item => item.Entry.Descriptor.ToolName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static double CalculateLexicalScore(
        ToolCatalogEntry entry,
        string query,
        IReadOnlySet<string> searchTerms)
    {
        if (searchTerms.Count == 0)
        {
            return 0d;
        }

        var corpus = BuildSearchTerms(entry.Document);

        var score = 0d;
        foreach (var term in searchTerms)
        {
            if (corpus.Contains(term))
            {
                score += 1d;
                continue;
            }

            if (corpus.Any(candidate =>
                    candidate.StartsWith(term, StringComparison.OrdinalIgnoreCase) ||
                    term.StartsWith(candidate, StringComparison.OrdinalIgnoreCase)))
            {
                score += 0.35d;
            }
        }

        if (entry.Descriptor.ToolName.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            score += 2d;
        }

        return score;
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

    private static McpGatewaySearchMatch ToSearchMatch(ToolCatalogEntry entry, double score)
        => new(
            entry.Descriptor.ToolId,
            entry.Descriptor.SourceId,
            entry.Descriptor.SourceKind,
            entry.Descriptor.ToolName,
            entry.Descriptor.DisplayName,
            entry.Descriptor.Description,
            entry.Descriptor.RequiredArguments,
            entry.Descriptor.InputSchemaJson,
            score);

    private static InvocationResolution ResolveInvocationTarget(
        ToolCatalogSnapshot snapshot,
        McpGatewayInvokeRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.ToolId))
        {
            var byToolId = snapshot.Entries.FirstOrDefault(item =>
                string.Equals(item.Descriptor.ToolId, request.ToolId, StringComparison.OrdinalIgnoreCase));
            return byToolId is null
                ? InvocationResolution.Fail(string.Format(CultureInfo.InvariantCulture, ToolIdNotFoundMessageFormat, request.ToolId))
                : InvocationResolution.Success(byToolId);
        }

        if (string.IsNullOrWhiteSpace(request.ToolName))
        {
            return InvocationResolution.Fail(ToolIdOrToolNameRequiredMessage);
        }

        var candidates = snapshot.Entries
            .Where(item => string.Equals(item.Descriptor.ToolName, request.ToolName, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(request.SourceId) ||
                           string.Equals(item.Descriptor.SourceId, request.SourceId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return candidates.Count switch
        {
            0 => InvocationResolution.Fail(string.Format(CultureInfo.InvariantCulture, ToolIdNotFoundMessageFormat, request.ToolName)),
            1 => InvocationResolution.Success(candidates[0]),
            _ => InvocationResolution.Fail(
                string.Format(CultureInfo.InvariantCulture, ToolNameAmbiguousMessageFormat, request.ToolName))
        };
    }

    private static object? ExtractMcpOutput(CallToolResult result)
    {
        if (result.StructuredContent is JsonElement element)
        {
            return element.Clone();
        }

        var text = result.Content?
            .OfType<TextContentBlock>()
            .FirstOrDefault(static block => !string.IsNullOrWhiteSpace(block.Text))
            ?.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return text;
        }
    }

    private static object? NormalizeFunctionOutput(object? value)
    {
        return value switch
        {
            JsonElement element => NormalizeJsonElement(element),
            JsonDocument document => NormalizeJsonElement(document.RootElement),
            _ => value
        };
    }

    private static object? NormalizeJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.True or JsonValueKind.False => element.GetBoolean(),
            JsonValueKind.Number when element.TryGetInt64(out var int64Value) => int64Value,
            JsonValueKind.Number when element.TryGetDecimal(out var decimalValue) => decimalValue,
            JsonValueKind.Number when element.TryGetDouble(out var doubleValue) => doubleValue,
            _ => element.Clone()
        };
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed record InvocationResolution(bool IsSuccess, ToolCatalogEntry? Entry, string? Error)
    {
        public static InvocationResolution Success(ToolCatalogEntry entry) => new(true, entry, null);

        public static InvocationResolution Fail(string error) => new(false, null, error);
    }

    private sealed record ScoredToolEntry(ToolCatalogEntry Entry, double Score);

    private sealed record ToolCatalogEntry(
        McpGatewayToolDescriptor Descriptor,
        AITool Tool,
        string Document,
        float[]? Vector = null,
        double Magnitude = 0d);

    private sealed record ToolCatalogSnapshot(IReadOnlyList<ToolCatalogEntry> Entries, bool HasVectors)
    {
        public static ToolCatalogSnapshot Empty { get; } = new([], false);
    }

    private sealed class EmbeddingGeneratorLease(
        IEmbeddingGenerator<string, Embedding<float>>? generator,
        AsyncServiceScope? scope = null)
        : IAsyncDisposable
    {
        public IEmbeddingGenerator<string, Embedding<float>>? Generator { get; } = generator;

        public ValueTask DisposeAsync() => scope?.DisposeAsync() ?? ValueTask.CompletedTask;
    }
}
