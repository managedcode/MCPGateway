using System.Collections.Concurrent;
using ManagedCode.MCPGateway.Abstractions;

namespace ManagedCode.MCPGateway;

public sealed class McpGatewayServerBinding(
    IMcpGateway gateway,
    IMcpGatewayPromptCatalog promptCatalog,
    IMcpGatewayResourceCatalog resourceCatalog,
    IMcpGatewayRegistry registry,
    Func<Action, IDisposable>? subscribeToPromptListChanges = null,
    Func<CancellationToken, ValueTask<IReadOnlyList<IMcpGatewayServerSource>>>? listSourcesAsync = null,
    Func<ValueTask>? disposeAsync = null
) : IMcpGatewayServerBinding
{
    private const string MissingSourcesMessage =
        "The current MCP server binding does not expose source-level operations. Provide ListSourcesAsync when using a custom binding implementation.";

    private static readonly IDisposable NoOpSubscription = new NoOpDisposable();
    private readonly ConcurrentDictionary<string, SourceAdapterEntry> _sourceAdapters = new(
        StringComparer.Ordinal
    );

    public IMcpGateway Gateway { get; } = gateway ?? throw new ArgumentNullException(nameof(gateway));

    public IMcpGatewayPromptCatalog PromptCatalog { get; } =
        promptCatalog ?? throw new ArgumentNullException(nameof(promptCatalog));

    public IMcpGatewayResourceCatalog ResourceCatalog { get; } =
        resourceCatalog ?? throw new ArgumentNullException(nameof(resourceCatalog));

    public IMcpGatewayRegistry Registry { get; } =
        registry ?? throw new ArgumentNullException(nameof(registry));

    public IDisposable SubscribeToPromptListChanges(Action onChanged)
    {
        ArgumentNullException.ThrowIfNull(onChanged);
        return subscribeToPromptListChanges?.Invoke(onChanged) ?? NoOpSubscription;
    }

    public ValueTask<IReadOnlyList<IMcpGatewayServerSource>> ListSourcesAsync(
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (listSourcesAsync is not null)
        {
            return listSourcesAsync(cancellationToken);
        }

        if (registry is not IMcpGatewayCatalogSource catalogSource)
        {
            throw new InvalidOperationException(MissingSourcesMessage);
        }

        var snapshot = catalogSource.CreateSnapshot();
        var activeSourceIds = new HashSet<string>(StringComparer.Ordinal);
        var sources = new List<IMcpGatewayServerSource>(snapshot.Registrations.Count);

        foreach (var registration in snapshot.Registrations)
        {
            activeSourceIds.Add(registration.SourceId);

            var entry = _sourceAdapters.AddOrUpdate(
                registration.SourceId,
                static (_, state) =>
                    new SourceAdapterEntry(
                        state.Registration,
                        new McpGatewayRegistrationBoundServerSource(state.Registration)
                    ),
                static (_, existing, state) =>
                    ReferenceEquals(existing.Registration, state.Registration)
                        ? existing
                        : new SourceAdapterEntry(
                            state.Registration,
                            new McpGatewayRegistrationBoundServerSource(state.Registration)
                        ),
                new SourceAdapterEntryState(registration)
            );

            sources.Add(entry.Source);
        }

        foreach (var sourceId in _sourceAdapters.Keys)
        {
            if (!activeSourceIds.Contains(sourceId))
            {
                _sourceAdapters.TryRemove(sourceId, out _);
            }
        }

        return ValueTask.FromResult<IReadOnlyList<IMcpGatewayServerSource>>(sources);
    }

    public ValueTask DisposeAsync() => disposeAsync?.Invoke() ?? ValueTask.CompletedTask;

    private sealed record SourceAdapterEntry(
        McpGatewayToolSourceRegistration Registration,
        IMcpGatewayServerSource Source
    );

    private sealed record SourceAdapterEntryState(McpGatewayToolSourceRegistration Registration);

    private sealed class NoOpDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
