using System.Collections.Concurrent;

namespace ManagedCode.MCPGateway;

/// <summary>
/// Process-wide pool of immutable indexes addressed by a content fingerprint.
/// </summary>
/// <remarks>
/// A lease keeps its index alive. Concurrent acquisitions of one fingerprint coalesce into one build, and a
/// failed build is forgotten so the next acquisition builds again. A waiter that stops waiting releases its lease;
/// the last one joins the build before it returns, so no build outlives every caller. Releasing the last lease
/// either retires the index or keeps it idle for reuse; at most <see cref="MaximumIdleIndexes"/> idle indexes stay
/// addressable and the least recently released idle index is retired first. A retired index is removed from the
/// pool before it is disposed, so a disposed index is never handed out. The caller whose release retires an
/// index awaits its disposal.
/// </remarks>
internal sealed class McpGatewaySharedIndexPool<TIndex>
    where TIndex : class, IAsyncDisposable
{
    /// <summary>Idle indexes the process-wide pool keeps addressable after their last lease is released.</summary>
    public const int DefaultMaximumIdleIndexes = 2;

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private long _releaseSequence;
    private int _buildCount;

    public McpGatewaySharedIndexPool(int maximumIdleIndexes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumIdleIndexes);
        MaximumIdleIndexes = maximumIdleIndexes;
    }

    public static McpGatewaySharedIndexPool<TIndex> Shared { get; } = new(DefaultMaximumIdleIndexes);

    public int MaximumIdleIndexes { get; }

    /// <summary>Number of index builds this pool has started.</summary>
    public int BuildCount => Volatile.Read(ref _buildCount);

    /// <summary>Indexes that are currently addressable by fingerprint, whether leased, building or idle.</summary>
    public int Count => _entries.Count;

    /// <summary>Built indexes that are addressable but hold no lease.</summary>
    public int IdleCount
    {
        get
        {
            var count = 0;
            foreach (var pair in _entries)
            {
                if (pair.Value.IsIdle)
                {
                    count++;
                }
            }

            return count;
        }
    }

    public async Task<Lease> AcquireAsync(
        string fingerprint,
        Func<Task<TIndex>> build,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(fingerprint);
        ArgumentNullException.ThrowIfNull(build);

        while (!cancellationToken.IsCancellationRequested)
        {
            var entry = _entries.GetOrAdd(fingerprint, static (key, pool) => new Entry(key, pool), this);
            if (!entry.TryAddLease())
            {
                Forget(entry);
                continue;
            }

            var startedBuild = entry.TryStart(build);
            TIndex index;
            try
            {
                index = await entry.Index.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await ReleaseAsync(entry, keepForReuse: true).ConfigureAwait(false);
                throw;
            }

            return new Lease(this, entry, index, isReused: !startedBuild);
        }

        throw new OperationCanceledException(cancellationToken);
    }

    private void Forget(Entry entry) =>
        _entries.TryRemove(new KeyValuePair<string, Entry>(entry.Fingerprint, entry));

    private async ValueTask ReleaseAsync(Entry entry, bool keepForReuse)
    {
        entry.MarkReleased(Interlocked.Increment(ref _releaseSequence));
        if (entry.RemoveLease() > 0)
        {
            return;
        }

        // The last lease of a build in progress joins it, so a canceled caller leaves no build running behind it.
        await ((Task)entry.Index).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (keepForReuse && entry.Index.IsCompletedSuccessfully)
        {
            await TrimIdleAsync().ConfigureAwait(false);
            return;
        }

        if (entry.TryRetire() is { } retirement)
        {
            await retirement.ConfigureAwait(false);
        }
    }

    private async ValueTask TrimIdleAsync()
    {
        List<Task>? retirements = null;
        var candidate = FindOldestIdleBeyondBound();
        while (candidate is not null)
        {
            if (candidate.TryRetire() is { } retirement)
            {
                (retirements ??= []).Add(retirement);
            }

            candidate = FindOldestIdleBeyondBound();
        }

        if (retirements is not null)
        {
            await Task.WhenAll(retirements).ConfigureAwait(false);
        }
    }

    private Entry? FindOldestIdleBeyondBound()
    {
        Entry? oldest = null;
        var idleCount = 0;
        foreach (var pair in _entries)
        {
            var entry = pair.Value;
            if (!entry.IsIdle)
            {
                continue;
            }

            idleCount++;
            if (oldest is null || entry.ReleasedAt < oldest.ReleasedAt)
            {
                oldest = entry;
            }
        }

        return idleCount > MaximumIdleIndexes ? oldest : null;
    }

    /// <summary>One acquired index. Releasing it is idempotent; the index must not be used afterwards.</summary>
    internal sealed class Lease
    {
        private readonly McpGatewaySharedIndexPool<TIndex> _pool;
        private Entry? _entry;

        internal Lease(McpGatewaySharedIndexPool<TIndex> pool, Entry entry, TIndex index, bool isReused)
        {
            _pool = pool;
            _entry = entry;
            Index = index;
            IsReused = isReused;
        }

        public TIndex Index { get; }

        /// <summary>True when this acquisition joined an index that another acquisition built or was building.</summary>
        public bool IsReused { get; }

        /// <summary>
        /// Releases the lease. With <paramref name="keepForReuse"/> the last release keeps a built index idle within
        /// the pool bound; otherwise the last release retires it.
        /// </summary>
        public ValueTask ReleaseAsync(bool keepForReuse) =>
            Interlocked.Exchange(ref _entry, null) is { } entry
                ? _pool.ReleaseAsync(entry, keepForReuse)
                : ValueTask.CompletedTask;
    }

    internal sealed class Entry(string fingerprint, McpGatewaySharedIndexPool<TIndex> pool)
    {
        private const int RetiredLeaseCount = -1;
        private const int NotStarted = 0;
        private const int Started = 1;
        private readonly TaskCompletionSource<TIndex> _index = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _leases;
        private int _started;
        private long _releasedAt;

        public string Fingerprint { get; } = fingerprint;

        public Task<TIndex> Index => _index.Task;

        public long ReleasedAt => Volatile.Read(ref _releasedAt);

        public bool IsIdle => Volatile.Read(ref _leases) == 0 && _index.Task.IsCompletedSuccessfully;

        public bool TryAddLease()
        {
            var leases = Volatile.Read(ref _leases);
            while (leases != RetiredLeaseCount && !_index.Task.IsFaulted && !_index.Task.IsCanceled)
            {
                var observed = Interlocked.CompareExchange(ref _leases, leases + 1, leases);
                if (observed == leases)
                {
                    return true;
                }

                leases = observed;
            }

            return false;
        }

        public int RemoveLease() => Interlocked.Decrement(ref _leases);

        public void MarkReleased(long sequence) => Volatile.Write(ref _releasedAt, sequence);

        public bool TryStart(Func<Task<TIndex>> build)
        {
            if (Interlocked.CompareExchange(ref _started, Started, NotStarted) != NotStarted)
            {
                return false;
            }

            Interlocked.Increment(ref pool._buildCount);
            _ = BuildAsync(build);
            return true;
        }

        /// <summary>Retires an entry that holds no lease; returns the disposal to await, or null when leased.</summary>
        public Task? TryRetire()
        {
            if (Interlocked.CompareExchange(ref _leases, RetiredLeaseCount, 0) != 0)
            {
                return null;
            }

            pool.Forget(this);
            return DisposeAsync();
        }

        private async Task BuildAsync(Func<Task<TIndex>> build)
        {
            try
            {
                _index.SetResult(await build().ConfigureAwait(false));
            }
            catch (Exception exception)
            {
                pool.Forget(this);
                _index.SetException(exception);
            }
        }

        private async Task DisposeAsync()
        {
            await ((Task)_index.Task).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (_index.Task.IsCompletedSuccessfully)
            {
                await _index.Task.Result.DisposeAsync().ConfigureAwait(false);
            }
            else if (_index.Task.Exception is { } failure)
            {
                // Lease holders that awaited the build received this failure; reading it marks it observed.
                _ = failure.InnerExceptions.Count;
            }
        }
    }
}
