using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace ManagedCode.MCPGateway;

/// <summary>One bounded catalog state in a host-owned cache. Eviction closes this store.</summary>
public sealed class McpGatewayInMemoryCatalogDefinitionStore : IMcpGatewayCatalogDefinitionStore, IDisposable
{
    private readonly IMemoryCache _cache;
    private readonly object _key = new();
    private readonly McpGatewayCatalogStoreOptions _options;
    private readonly State _state = new();

    public McpGatewayInMemoryCatalogDefinitionStore(IMemoryCache cache, IOptions<McpGatewayCatalogStoreOptions> options)
    {
        _cache = cache;
        _options = options.Value;
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxDefinitions, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxBytes, 1);
        _cache.Set(_key, _state, new MemoryCacheEntryOptions { Size = _options.MaxBytes }
            .RegisterPostEvictionCallback(static (_, value, _, _) => ((State)value!).Close()));
    }

    public ValueTask<McpGatewayPublishedCatalog> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_cache.TryGetValue(_key, out State? state) && state is not null ? state.Read() : McpGatewayPublishedCatalog.Empty);
    }

    public ValueTask<bool> CompareExchangeAsync(long expectedRevision, McpGatewayPublishedCatalog candidate, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.Revision != expectedRevision + 1 || candidate.Definitions.Length > _options.MaxDefinitions
            || candidate.RevokedToolIds.Count > _options.MaxDefinitions
            || JsonSerializer.SerializeToUtf8Bytes(candidate, McpGatewayJsonSerializer.Options).Length > _options.MaxBytes)
        {
            return ValueTask.FromResult(false);
        }
        return ValueTask.FromResult(_cache.TryGetValue(_key, out State? state) && state is not null && state.Publish(expectedRevision, candidate));
    }

    public void Dispose()
    {
        _state.Close();
        _cache.Remove(_key);
    }

    private sealed class State
    {
        private McpGatewayPublishedCatalog? _current = McpGatewayPublishedCatalog.Empty;
        public McpGatewayPublishedCatalog Read() => Volatile.Read(ref _current) ?? McpGatewayPublishedCatalog.Empty;
        public bool Publish(long expectedRevision, McpGatewayPublishedCatalog candidate)
        {
            var current = Volatile.Read(ref _current);
            return current is not null && current.Revision == expectedRevision
                && ReferenceEquals(Interlocked.CompareExchange(ref _current, candidate, current), current);
        }
        public void Close() => Interlocked.Exchange(ref _current, null);
    }
}
