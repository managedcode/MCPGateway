using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace ManagedCode.MCPGateway;

/// <summary>Bounded retention using the host's cache and clock; this type owns no hidden cache.</summary>
public sealed class McpGatewayInMemoryResultStore : IMcpGatewayResultStore, IDisposable
{
    private const int GuidHandleLength = 32;
    private readonly IMemoryCache _cache;
    private readonly TimeProvider _clock;
    private readonly McpGatewayResultStoreOptions _options;
    private readonly object _cacheScope = new();
    private readonly ConcurrentDictionary<string, StoredEntry> _owned = new(StringComparer.Ordinal);
    private readonly ITimer _cleanup;
    private int _disposed;
    private int _entries;
    private long _bytes;

    public McpGatewayInMemoryResultStore(IMemoryCache cache, IOptions<McpGatewayResultStoreOptions> options, TimeProvider clock)
    {
        _cache = cache;
        _clock = clock;
        _options = options.Value;
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxEntries, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxStoredBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxResultBytes, 1);
        if (_options.TimeToLive <= TimeSpan.Zero || _options.CleanupInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Result retention must be positive.");
        }
        _cleanup = clock.CreateTimer(static state => ((McpGatewayInMemoryResultStore)state!).CleanupExpired(),
            this, _options.CleanupInterval, _options.CleanupInterval);
    }

    public ValueTask<McpGatewayStoredResult?> StoreAsync(McpGatewayExecutionContext context, JsonElement result,
        McpGatewayToolDescriptor tool, CancellationToken cancellationToken = default) => StoreCore(context, result, tool, null, cancellationToken);

    public ValueTask<McpGatewayStoredResult?> StoreAsync(McpGatewayExecutionContext context, JsonElement result,
        McpGatewayToolDescriptor tool, int maximumDeliveryBytes, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumDeliveryBytes, 1);
        return StoreCore(context, result, tool, maximumDeliveryBytes, cancellationToken);
    }

    public ValueTask<McpGatewayStoredResult?> StoreAsync(McpGatewayExecutionContext context, JsonElement result,
        CancellationToken cancellationToken = default) => StoreCore(context, result, null, null, cancellationToken);

    private ValueTask<McpGatewayStoredResult?> StoreCore(McpGatewayExecutionContext context, JsonElement result, McpGatewayToolDescriptor? tool,
        int? maximumDeliveryBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(context);
        CleanupExpired();
        var size = Encoding.UTF8.GetByteCount(result.GetRawText());
        if (!context.IsAuthenticated || size > _options.MaxResultBytes)
        {
            return ValueTask.FromResult<McpGatewayStoredResult?>(null);
        }
        if (Interlocked.Increment(ref _entries) > _options.MaxEntries)
        {
            Interlocked.Decrement(ref _entries);
            return ValueTask.FromResult<McpGatewayStoredResult?>(null);
        }
        if (Interlocked.Add(ref _bytes, size) > _options.MaxStoredBytes)
        {
            Release(size);
            return ValueTask.FromResult<McpGatewayStoredResult?>(null);
        }
        var stored = new McpGatewayStoredResult(Guid.NewGuid().ToString("N"), result.Clone(), _clock.GetUtcNow() + _options.TimeToLive)
        {
            ToolId = tool?.ToolId,
            SourceId = tool?.SourceId,
            DefinitionHash = tool?.DefinitionHash,
            RequiresPublication = tool?.RequiresPublication is true,
            MaximumDeliveryBytes = maximumDeliveryBytes
        };
        var entry = new StoredEntry(context.SubjectId, context.TenantId, context.AuthorizationPartition, context.PolicyRevision, stored);
        var cacheOptions = new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = _options.TimeToLive, Size = size };
        _owned[stored.Handle] = entry;
        cacheOptions.RegisterPostEvictionCallback((_, _, _, _) => ReleaseEntry(entry, size));
        try
        {
            _cache.Set((_cacheScope, stored.Handle), entry, cacheOptions);
            if (Volatile.Read(ref _disposed) != 0)
            {
                _cache.Remove((_cacheScope, stored.Handle));
                ReleaseEntry(entry, size);
                return ValueTask.FromResult<McpGatewayStoredResult?>(null);
            }
        }
        catch
        {
            ReleaseEntry(entry, size);
            throw;
        }
        return ValueTask.FromResult<McpGatewayStoredResult?>(stored);
    }

    public ValueTask<McpGatewayStoredResult?> ReadAsync(McpGatewayExecutionContext context, string handle, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(handle);
        if (!context.IsAuthenticated || handle.Length != GuidHandleLength || !_cache.TryGetValue<StoredEntry>((_cacheScope, handle), out var entry) || entry is null)
        {
            return ValueTask.FromResult<McpGatewayStoredResult?>(null);
        }
        if (entry.Value.ExpiresAt <= _clock.GetUtcNow())
        {
            _cache.Remove((_cacheScope, handle));
            ReleaseEntry(entry, Encoding.UTF8.GetByteCount(entry.Value.Result.GetRawText()));
            return ValueTask.FromResult<McpGatewayStoredResult?>(null);
        }
        var allowed = entry.Subject == context.SubjectId && entry.Tenant == context.TenantId
            && entry.Partition == context.AuthorizationPartition && entry.PolicyRevision == context.PolicyRevision;
        return ValueTask.FromResult(allowed ? entry.Value : null);
    }

    public async ValueTask<bool> RemoveAsync(McpGatewayExecutionContext context, string handle, CancellationToken cancellationToken = default)
    {
        if (await ReadAsync(context, handle, cancellationToken) is null)
        {
            return false;
        }
        _cache.Remove((_cacheScope, handle));
        if (_owned.TryGetValue(handle, out var entry))
        {
            ReleaseEntry(entry, Encoding.UTF8.GetByteCount(entry.Value.Result.GetRawText()));
        }
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _cleanup.Dispose();
        foreach (var entry in _owned.Values)
        {
            _cache.Remove((_cacheScope, entry.Value.Handle));
            ReleaseEntry(entry, Encoding.UTF8.GetByteCount(entry.Value.Result.GetRawText()));
        }
    }

    private void CleanupExpired()
    {
        foreach (var entry in _owned.Values)
        {
            if (entry.Value.ExpiresAt <= _clock.GetUtcNow())
            {
                _cache.Remove((_cacheScope, entry.Value.Handle));
                ReleaseEntry(entry, Encoding.UTF8.GetByteCount(entry.Value.Result.GetRawText()));
            }
        }
    }

    private void ReleaseEntry(StoredEntry entry, int size)
    {
        if (_owned.TryRemove(new KeyValuePair<string, StoredEntry>(entry.Value.Handle, entry)))
        {
            Release(size);
        }
    }

    private void Release(int size)
    {
        Interlocked.Decrement(ref _entries);
        Interlocked.Add(ref _bytes, -size);
    }

    private sealed record StoredEntry(string Subject, string Tenant, string Partition, string PolicyRevision, McpGatewayStoredResult Value);
}
