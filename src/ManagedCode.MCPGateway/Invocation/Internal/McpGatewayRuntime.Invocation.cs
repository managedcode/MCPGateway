using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime
{
    private readonly int _maximumManagedResponseBytes;
    public async Task<McpGatewayInvokeResult> InvokeAsync(
        McpGatewayInvokeRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        var executionContext = await McpGatewayTrustedContext.ResolveAsync(_serviceProvider, cancellationToken);

        using var activity = McpGatewayInvocationTelemetry.Start();
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        McpGatewayInvokeResult result;
        try
        { result = await InvokeCoreAsync(request, executionContext, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            McpGatewayInvocationTelemetry.Complete(activity, CreateDenied(request) with
            { Outcome = McpGatewayInvocationOutcome.Cancelled, ErrorCode = ExecutionCancelledCode },
                System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
        var duration = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        McpGatewayInvocationTelemetry.Complete(activity, result, duration);
        if (McpGatewayTrustedContext.OperationServices(_serviceProvider).GetService<IMcpGatewayAuditSink>() is { } audit)
        {
            try
            {
                using var auditTimeout = new CancellationTokenSource(_executionOptions.AuditTimeout,
                    _serviceProvider.GetService<TimeProvider>() ?? TimeProvider.System);
                await audit.RecordAsync(executionContext, new("invoke", result.ToolId, result.SourceId, result.DefinitionHash,
                    result.Outcome, result.ErrorCode, result.DeliveredBytes ?? 0, duration,
                    (_serviceProvider.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow()), auditTimeout.Token).AsTask().WaitAsync(auditTimeout.Token);
            }
            catch (Exception exception) when (!McpGatewayRuntimeFailureClassifier.HasFatalFailure(exception))
            {
                _logger.LogError("Gateway audit delivery failed.");
            }
        }
        return result;
    }

    private async Task<McpGatewayInvokeResult> InvokeCoreAsync(McpGatewayInvokeRequest request,
        McpGatewayExecutionContext executionContext, CancellationToken cancellationToken)
    {
        using var catalogLease = await AcquireCatalogLeaseAsync(cancellationToken);
        var snapshot = await GetSnapshotAsync(cancellationToken);
        if (snapshot.Version != catalogLease.Version || !ReferenceEquals(snapshot.SourceOwner, catalogLease.Owner))
        {
            return CreateDenied(request);
        }
        var resolution = ResolveInvocationTarget(snapshot, request);
        if (!resolution.IsSuccess || resolution.Entry is null)
        {
            return _managedCatalog || McpGatewayTrustedContext.OperationServices(_serviceProvider).GetService<IMcpGatewayAccessPolicy>() is not null
                ? CreateDenied(request)
                : CreateInvocationFailure(request.ToolId ?? string.Empty, request.SourceId ?? string.Empty,
                    request.ToolName ?? string.Empty, resolution.Error);
        }
        var entry = resolution.Entry;
        if (!await IsPublishedAsync(entry.Descriptor, cancellationToken))
        {
            return CreateDenied(request);
        }
        if (_managedCatalog)
        {
            var response = request.ResponseOptions ?? new McpGatewayResponseOptions();
            request = request with { ResponseOptions = response with { MaxBytes = Math.Min(response.MaxBytes, _maximumManagedResponseBytes) } };
        }
        var arguments = BuildInvocationArguments(request, entry.Descriptor);
        var valid = ValidateInvocation(entry, request, arguments);
        var descriptor = await GetApprovedDescriptorAsync(entry.Descriptor, cancellationToken);
        var decision = await McpGatewayAccessBoundary.AuthorizeAsync(_serviceProvider, executionContext,
            new(McpGatewayOperation.Invoke, descriptor.ToolId, descriptor.SourceId)
            {
                Arguments = valid ? arguments : null,
                RequiredPermissions = descriptor.RequiredPermissions,
                DefinitionHash = descriptor.DefinitionHash
            }, cancellationToken);
        if (!decision.IsAllowed)
        {
            return CreateDenied(request);
        }
        if (!valid)
        {
            return CreateInvalidArgumentsFailure(descriptor);
        }
        try
        {
            var result = await ExecuteAdmittedAsync(entry, request, arguments, executionContext, decision, catalogLease, cancellationToken);
            return await McpGatewayResultDelivery.DeliverAsync(_serviceProvider, descriptor, request,
                result with { DefinitionHash = descriptor.DefinitionHash },
                executionContext, entry.Validator, _managedCatalog, _maximumManagedResponseBytes, cancellationToken);
        }
        catch (Exception ex) when (McpGatewayRuntimeFailureClassifier.IsRecoverable(ex))
        {
            _logger.LogError(GatewayInvocationFailedLogMessage);
            return CreateInvocationFailure(descriptor.ToolId, descriptor.SourceId, descriptor.ToolName, InvocationFailedMessage);
        }
    }

    private static bool ValidateInvocation(ToolCatalogEntry entry, McpGatewayInvokeRequest request, Dictionary<string, object?> arguments)
    {
        try
        {
            if (request.ResponseOptions is { } response)
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(response.MaxBytes, 1);
                McpGatewayJsonProjection.Validate(response.SelectPaths);
            }
            return entry.Validator.ValidateArguments(arguments);
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException or InvalidOperationException or Json.Schema.JsonSchemaException or Json.Schema.RefResolutionException)
        {
            return false;
        }
    }

    private static McpGatewayInvokeResult CreateDenied(McpGatewayInvokeRequest request) =>
        new(false, request.ToolId ?? string.Empty, request.SourceId ?? string.Empty, request.ToolName ?? string.Empty,
            null, "The requested tool is unavailable.")
        { Outcome = McpGatewayInvocationOutcome.Denied, ErrorCode = McpGatewayInvocationErrors.Unavailable };

    private static McpGatewayInvokeResult CreateInvalidArgumentsFailure(McpGatewayToolDescriptor descriptor) =>
        new(false, descriptor.ToolId, descriptor.SourceId, descriptor.ToolName, null, "Tool arguments or response options are invalid.")
        { Outcome = McpGatewayInvocationOutcome.InvalidArguments, ErrorCode = McpGatewayInvocationErrors.InvalidArguments };

    private static Dictionary<string, object?> BuildInvocationArguments(
        McpGatewayInvokeRequest request,
        McpGatewayToolDescriptor descriptor
    )
    {
        var arguments = request.Arguments is { Count: > 0 }
            ? new Dictionary<string, object?>(request.Arguments, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        if (
            !string.IsNullOrWhiteSpace(request.Query)
            && !arguments.ContainsKey(QueryArgumentName)
            && SupportsArgument(descriptor, QueryArgumentName)
        )
        {
            arguments[QueryArgumentName] = request.Query;
        }

        MapRequestArgument(arguments, descriptor, ContextArgumentName, request.Context);
        MapRequestArgument(
            arguments,
            descriptor,
            ContextSummaryArgumentName,
            request.ContextSummary
        );
        return arguments;
    }

    private async Task<McpGatewayInvokeResult> InvokeResolvedToolAsync(
        ToolCatalogEntry entry,
        McpGatewayInvokeRequest request,
        Dictionary<string, object?> arguments,
        CancellationToken cancellationToken
    )
    {
        var resolvedMcpTool = entry.Tool as McpClientTool ?? entry.Tool.GetService<McpClientTool>();
        if (resolvedMcpTool is not null)
        {
            return await InvokeMcpToolAsync(
                entry,
                request,
                arguments,
                resolvedMcpTool,
                cancellationToken
            );
        }

        var function = entry.Tool as AIFunction ?? entry.Tool.GetService<AIFunction>();
        if (function is null)
        {
            return CreateInvocationFailure(
                entry.Descriptor.ToolId,
                entry.Descriptor.SourceId,
                entry.Descriptor.ToolName,
                string.Format(
                    CultureInfo.InvariantCulture,
                    ToolNotInvokableMessageFormat,
                    entry.Descriptor.ToolName
                )
            );
        }

        return await InvokeFunctionAsync(entry, arguments, function, cancellationToken);
    }

    private async Task<McpGatewayInvokeResult> InvokeMcpToolAsync(
        ToolCatalogEntry entry,
        McpGatewayInvokeRequest request,
        Dictionary<string, object?> arguments,
        McpClientTool tool,
        CancellationToken cancellationToken
    )
    {
        CallToolResult result;
        try
        {
            result = await AttachInvocationMeta(tool, request)
                .CallAsync(
                    arguments,
                    progress: null,
                    options: new RequestOptions(),
                    cancellationToken: cancellationToken
                );
        }
        catch (MissingRequiredClientCapabilityException exception)
            when (
                entry.Client is { } client
                && RequiresTasksCapability(exception)
                && SupportsTasks(client)
            )
        {
            result = await client.CallToolWithPollingAsync(
                new CallToolRequestParams
                {
                    Name = tool.ProtocolTool.Name,
                    Arguments = SerializeToolArguments(arguments),
                    Meta = BuildInvocationMeta(request),
                },
                _mcpTaskMaximumConsecutiveStuckPolls,
                cancellationToken
            );
        }

        return new McpGatewayInvokeResult(
            true,
            entry.Descriptor.ToolId,
            entry.Descriptor.SourceId,
            entry.Descriptor.ToolName,
            ExtractMcpOutput(result)
        )
        {
            McpResult = result,
            Outcome = result.IsError is true ? McpGatewayInvocationOutcome.ToolError : McpGatewayInvocationOutcome.Succeeded,
            ErrorCode = result.IsError is true ? McpGatewayInvocationErrors.ToolError : null,
        };
    }

    private static bool RequiresTasksCapability(
        MissingRequiredClientCapabilityException exception
    ) =>
        exception.RequiredCapabilities.Extensions?.ContainsKey(TasksProtocol.ExtensionId)
        is true;

    private static bool SupportsTasks(McpClient client) =>
        client.ServerCapabilities.Extensions?.ContainsKey(TasksProtocol.ExtensionId) is true;

    private static IDictionary<string, JsonElement>? SerializeToolArguments(
        IReadOnlyDictionary<string, object?> arguments
    )
    {
        if (arguments.Count == 0)
        {
            return null;
        }

        var serialized = McpGatewayJsonSerializer.TrySerializeToElement(arguments)
            ?? throw new JsonException("Tool arguments could not be serialized.");
        return serialized.Deserialize<Dictionary<string, JsonElement>>(
            McpGatewayJsonSerializer.Options
        );
    }

    private static async Task<McpGatewayInvokeResult> InvokeFunctionAsync(
        ToolCatalogEntry entry,
        Dictionary<string, object?> arguments,
        AIFunction function,
        CancellationToken cancellationToken
    )
    {
        var resultValue = await function.InvokeAsync(
            new AIFunctionArguments(arguments, StringComparer.OrdinalIgnoreCase),
            cancellationToken
        );

        return new McpGatewayInvokeResult(
            true,
            entry.Descriptor.ToolId,
            entry.Descriptor.SourceId,
            entry.Descriptor.ToolName,
            NormalizeFunctionOutput(resultValue)
        );
    }

    private static McpGatewayInvokeResult CreateInvocationFailure(
        string toolId,
        string sourceId,
        string toolName,
        string? error
    ) => new(false, toolId, sourceId, toolName, Output: null, Error: error);

    private static void MapRequestArgument(
        IDictionary<string, object?> arguments,
        McpGatewayToolDescriptor descriptor,
        string argumentName,
        object? value
    )
    {
        if (
            value is null
            || arguments.ContainsKey(argumentName)
            || !SupportsArgument(descriptor, argumentName)
        )
        {
            return;
        }

        if (value is string text && string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        arguments[argumentName] = value;
    }

    private static bool SupportsArgument(McpGatewayToolDescriptor descriptor, string argumentName)
    {
        if (descriptor.RequiredArguments.Contains(argumentName, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        var schema = descriptor.InputSchema;
        if (schema.ValueKind == JsonValueKind.Undefined)
        {
            return false;
        }

        if (
            !schema.TryGetProperty(InputSchemaPropertiesPropertyName, out var properties)
            || properties.ValueKind != JsonValueKind.Object
        )
        {
            return false;
        }

        return properties
            .EnumerateObject()
            .Any(property =>
                string.Equals(property.Name, argumentName, StringComparison.OrdinalIgnoreCase)
            );
    }

    private McpClientTool AttachInvocationMeta(
        McpClientTool tool,
        McpGatewayInvokeRequest request
    )
    {
        var meta = BuildInvocationMeta(request);
        return meta is null ? tool : tool.WithMeta(meta);
    }

    private JsonObject? BuildInvocationMeta(McpGatewayInvokeRequest request)
    {
        var payload = new JsonObject();
        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            payload[QueryArgumentName] = request.Query.Trim();
        }

        var restricted = _managedCatalog || _executionOptions.RestrictForwardedContext;
        if ((!restricted || _executionOptions.ForwardContextSummary) && !string.IsNullOrWhiteSpace(request.ContextSummary))
        {
            payload[ContextSummaryArgumentName] = request.ContextSummary.Trim();
        }

        if (request.Context is { Count: > 0 })
        {
            var contextNode = McpGatewayJsonSerializer.TrySerializeToNode(restricted
                ? request.Context.Where(pair => _executionOptions.ForwardedContextKeys.Contains(pair.Key))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
                : request.Context);
            if (contextNode is not null)
            {
                payload[ContextArgumentName] = contextNode;
            }
        }

        return payload.Count == 0 ? null : new JsonObject { [GatewayInvocationMetaKey] = payload };
    }
}
