using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime
{
    private readonly McpGatewayExecutionOptions _executionOptions;
    private const string CapacityUnavailableCode = "source_capacity_unavailable";
    private const string ExecutionCancelledCode = "execution_cancelled";
    private const string BindingUnavailableCode = "credential_binding_unavailable";
    private const string TransportCompletionUnknownCode = "transport_completion_unknown";
    private const string ExecutionFailedMessage = "The gateway could not confirm successful execution.";

    private async Task<McpGatewayInvokeResult> ExecuteAdmittedAsync(ToolCatalogEntry entry, McpGatewayInvokeRequest request,
        Dictionary<string, object?> arguments, McpGatewayExecutionContext context, McpGatewayAccessDecision decision,
        McpGatewayCatalogSourceSnapshot catalog, CancellationToken cancellationToken)
    {
        var bounded = _executionOptions.Enabled || _managedCatalog;
        var enforceTimeout = bounded || context.Deadline.HasValue;
        var clock = _serviceProvider.GetService<TimeProvider>() ?? TimeProvider.System;
        var duration = _executionOptions.Sources.GetValueOrDefault(entry.Descriptor.SourceId)?.ExecutionTimeout
            ?? McpGatewayExecutionOptions.DefaultExecutionTimeout;
        if (context.Deadline is { } deadline)
        {
            duration = TimeSpan.FromTicks(Math.Min(duration.Ticks, (deadline - clock.GetUtcNow()).Ticks));
        }
        if (duration <= TimeSpan.Zero)
        {
            return ExecutionFailure(entry.Descriptor, McpGatewayInvocationOutcome.TimedOut, ExecutionCancelledCode);
        }
        var lifetime = new ExecutionLifetime(enforceTimeout ? new CancellationTokenSource(duration, clock) : null,
            decision.IsReadOnly, cancellationToken);
        var work = ExecuteOwnedAsync(entry, request, arguments, context, catalog, bounded, lifetime);
        try
        {
            return enforceTimeout ? await work.WaitAsync(lifetime.Token) : await work;
        }
        catch (OperationCanceledException) when (enforceTimeout && lifetime.Token.IsCancellationRequested)
        {
            var outcome = lifetime.Dispatched && !lifetime.IsReadOnly ? McpGatewayInvocationOutcome.UnknownCompletion
                : cancellationToken.IsCancellationRequested ? McpGatewayInvocationOutcome.Cancelled : McpGatewayInvocationOutcome.TimedOut;
            return ExecutionFailure(entry.Descriptor, outcome, ExecutionCancelledCode);
        }
    }

    // This task retains all execution resources after a caller deadline until the actual dispatch completes.
    private async Task<McpGatewayInvokeResult> ExecuteOwnedAsync(ToolCatalogEntry entry, McpGatewayInvokeRequest request,
        Dictionary<string, object?> arguments, McpGatewayExecutionContext context, McpGatewayCatalogSourceSnapshot catalog,
        bool bounded, ExecutionLifetime lifetime)
    {
        using (lifetime)
        using (var lease = _catalogSource.CreateSnapshot())
        {
            if (lease.Version != catalog.Version || !ReferenceEquals(lease.Owner, catalog.Owner))
            {
                return CreateDenied(request);
            }
            await using var services = _serviceProvider.CreateAsyncScope();
            using var trusted = _serviceProvider.GetRequiredService<McpGatewayExecutionScope>()
                .Push(context, services.ServiceProvider, entry.Descriptor.ToolId, null);
            var admissionStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            var sourceAdmission = (_serviceProvider as IMcpGatewayAdmissionProvider)?.Admission
                ?? services.ServiceProvider.GetRequiredService<IMcpGatewaySourceAdmission>();
            await using var admission = bounded ? await sourceAdmission
                .TryEnterAsync(context, entry.Descriptor.SourceId, lifetime.Token) : null;
            if (bounded)
            { McpGatewayInvocationTelemetry.Admission(System.Diagnostics.Stopwatch.GetElapsedTime(admissionStarted).TotalMilliseconds); }
            if (bounded && admission is null)
            {
                return ExecutionFailure(entry.Descriptor, McpGatewayInvocationOutcome.RateLimited, CapacityUnavailableCode);
            }
            try
            {
                await using var client = await ResolveExecutionClientAsync(entry, context, lifetime.Token);
                var resolved = client is null ? entry : await BindExecutionClientAsync(entry, client.Client, lifetime.Token);
                using var current = _catalogSource.CreateSnapshot();
                var descriptor = await GetApprovedDescriptorAsync(entry.Descriptor, lifetime.Token);
                var allowed = await McpGatewayAccessBoundary.AuthorizeAsync(services.ServiceProvider, context,
                    new(McpGatewayOperation.Invoke, descriptor.ToolId, descriptor.SourceId)
                    { Arguments = arguments, RequiredPermissions = descriptor.RequiredPermissions, DefinitionHash = descriptor.DefinitionHash }, lifetime.Token);
                if (current.Version != lease.Version || !ReferenceEquals(current.Owner, lease.Owner)
                    || !allowed.IsAllowed || !await IsPublishedAsync(entry.Descriptor, lifetime.Token))
                {
                    return CreateDenied(request);
                }
                lifetime.IsReadOnly = allowed.IsReadOnly;
                lifetime.Token.ThrowIfCancellationRequested();
                lifetime.Dispatched = true;
                var dispatchStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                McpGatewayInvokeResult result;
                try
                { result = await InvokeResolvedToolAsync(resolved, request, arguments, lifetime.Token); }
                finally { McpGatewayInvocationTelemetry.Dispatch(System.Diagnostics.Stopwatch.GetElapsedTime(dispatchStarted).TotalMilliseconds); }
                admission?.Complete(false);
                return result;
            }
            catch (UnauthorizedAccessException)
            {
                return ExecutionFailure(entry.Descriptor, McpGatewayInvocationOutcome.Denied, BindingUnavailableCode);
            }
            catch (OperationCanceledException) when (lifetime.Token.IsCancellationRequested)
            {
                return ExecutionFailure(entry.Descriptor, lifetime.Dispatched && !lifetime.IsReadOnly
                    ? McpGatewayInvocationOutcome.UnknownCompletion : lifetime.CallerCancellationRequested
                        ? McpGatewayInvocationOutcome.Cancelled : McpGatewayInvocationOutcome.TimedOut, ExecutionCancelledCode);
            }
            catch (Exception exception) when (McpGatewayRuntimeFailureClassifier.IsRecoverable(exception))
            {
                if (exception is ModelContextProtocol.McpProtocolException { ErrorCode: ModelContextProtocol.McpErrorCode.InvalidParams })
                {
                    admission?.Complete(false);
                    return CreateInvalidArgumentsFailure(entry.Descriptor);
                }
                var transportFailure = exception is IOException or HttpRequestException or ModelContextProtocol.McpException;
                admission?.Complete(transportFailure);
                _logger.LogError(GatewayInvocationFailedLogMessage);
                return transportFailure && lifetime.Dispatched && !lifetime.IsReadOnly
                    ? ExecutionFailure(entry.Descriptor, McpGatewayInvocationOutcome.UnknownCompletion, TransportCompletionUnknownCode)
                    : CreateInvocationFailure(entry.Descriptor.ToolId, entry.Descriptor.SourceId, entry.Descriptor.ToolName, InvocationFailedMessage);
            }
        }
    }

    private sealed class ExecutionLifetime : IDisposable
    {
        private readonly CancellationTokenSource? _timeout;
        private readonly CancellationTokenSource _linked;
        private readonly CancellationToken _caller;
        public ExecutionLifetime(CancellationTokenSource? timeout, bool isReadOnly, CancellationToken caller)
        {
            _timeout = timeout;
            _caller = caller;
            _linked = timeout is null ? CancellationTokenSource.CreateLinkedTokenSource(caller)
                : CancellationTokenSource.CreateLinkedTokenSource(caller, timeout.Token);
            Token = _linked.Token;
            IsReadOnly = isReadOnly;
        }
        public CancellationToken Token { get; }
        public bool CallerCancellationRequested => _caller.IsCancellationRequested;
        public volatile bool Dispatched;
        public volatile bool IsReadOnly;
        public void Dispose() { _linked.Dispose(); _timeout?.Dispose(); }
    }

    private async ValueTask<IMcpGatewayExecutionClientLease?> ResolveExecutionClientAsync(ToolCatalogEntry entry,
        McpGatewayExecutionContext context, CancellationToken cancellationToken)
    {
        if (entry.Client is null)
        {
            return null;
        }
        if (!_executionOptions.RemoteBindings.TryGetValue(entry.Descriptor.SourceId, out var binding))
        {
            if (_managedCatalog)
            {
                throw new InvalidOperationException("Managed remote execution requires an explicit reviewed credential binding.");
            }
            return null;
        }
        if (binding.UseSharedTechnicalClient)
        {
            return null;
        }
        var lease = await McpGatewayTrustedContext.OperationServices(_serviceProvider).GetRequiredService<IMcpGatewayExecutionClientResolver>().ResolveAsync(context, binding, cancellationToken);
        if (ReferenceEquals(lease.Client, entry.Client) || lease.BindingReference != binding.BindingReference || lease.Audience != binding.Audience
            || lease.CredentialRevision != context.CredentialRevision || binding.RequiredScopes.Any(scope => !lease.GrantedScopes.Contains(scope)))
        {
            await lease.DisposeAsync();
            throw new UnauthorizedAccessException("The execution credential binding is unavailable.");
        }
        return lease;
    }

    private static async Task<ToolCatalogEntry> BindExecutionClientAsync(ToolCatalogEntry entry, McpClient client, CancellationToken cancellationToken)
    {
        var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
        var tool = tools.FirstOrDefault(candidate => candidate.Name.Equals(entry.Tool.Name, StringComparison.Ordinal));
        var reviewedProtocol = (entry.Tool as McpClientTool)?.ProtocolTool;
        if (tool is null || reviewedProtocol is null
            || McpGatewayDefinitionHash.Compute(tool.ProtocolTool) != McpGatewayDefinitionHash.Compute(reviewedProtocol))
        {
            throw new UnauthorizedAccessException("The caller-bound tool no longer matches the approved schema.");
        }
        return entry with { Tool = tool, Client = client };
    }

    private static McpGatewayInvokeResult ExecutionFailure(McpGatewayToolDescriptor tool, McpGatewayInvocationOutcome outcome, string code) =>
        new(false, tool.ToolId, tool.SourceId, tool.ToolName, null, ExecutionFailedMessage)
        { Outcome = outcome, ErrorCode = code };
}
