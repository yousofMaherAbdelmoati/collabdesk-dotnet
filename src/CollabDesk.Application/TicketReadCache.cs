using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace CollabDesk.Application;

/// <summary>
/// Best-effort distributed read cache. Cache failures are deliberately never allowed to
/// change the database-backed result of a request.
/// </summary>
public sealed class TicketReadCache(IDistributedCache cache, ILogger<TicketReadCache> logger)
{
    private const string Prefix = "collabdesk:v1";
    // Locks are retained only while callers are waiting or loading. Cache keys contain a
    // version token, so keeping a SemaphoreSlim for every key would otherwise leak memory.
    private static readonly ConcurrentDictionary<string, MissLock> MissLocks = new(StringComparer.Ordinal);
    private static readonly Meter Meter = new("CollabDesk.Cache", "1.0");
    private static readonly Counter<long> Hits = Meter.CreateCounter<long>("collabdesk.cache.hits");
    private static readonly Counter<long> Misses = Meter.CreateCounter<long>("collabdesk.cache.misses");
    private static readonly Counter<long> Failures = Meter.CreateCounter<long>("collabdesk.cache.failures");
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private static readonly DistributedCacheEntryOptions TicketTtl = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2)
    };

    public async Task<T> GetTicketListAsync<T>(Guid userId, string role, string status, int page, int pageSize,
        Func<CancellationToken, Task<T>> load, CancellationToken ct) =>
        await GetOrCreateAsync($"{Prefix}:ticket-lists:user:{userId:N}:role:{role}:status:{status}:page:{page}:size:{pageSize}:v:{await VersionAsync(CustomerListVersionKey(userId), ct)}",
            "ticket-list", TicketTtl, load, ct);

    public async Task<T> GetAgentTicketListAsync<T>(Guid agentId, string status, int page, int pageSize,
        Func<CancellationToken, Task<T>> load, CancellationToken ct) =>
        await GetOrCreateAsync($"{Prefix}:ticket-lists:agent:{agentId:N}:status:{status}:page:{page}:size:{pageSize}:queue-v:{await VersionAsync(AgentQueueVersionKey, ct)}:agent-v:{await VersionAsync(AgentVersionKey(agentId), ct)}",
            "ticket-list", TicketTtl, load, ct);

    public async Task<T> GetMessagesAsync<T>(long ticketId, string kind, long cursor, int pageSize,
        Func<CancellationToken, Task<T>> load, CancellationToken ct) =>
        await GetOrCreateAsync($"{Prefix}:ticket-messages:ticket:{ticketId}:kind:{kind}:cursor:{cursor}:size:{pageSize}:v:{await VersionAsync(MessageVersionKey(ticketId), ct)}",
            "ticket-messages", TicketTtl, load, ct);

    public async Task<T> GetAgentStatsAsync<T>(Guid agentId, Func<CancellationToken, Task<T>> load, CancellationToken ct) =>
        await GetOrCreateAsync($"{Prefix}:agent-stats:agent:{agentId:N}:queue-v:{await VersionAsync(AgentQueueVersionKey, ct)}:agent-v:{await VersionAsync(AgentVersionKey(agentId), ct)}",
            "agent-stats", TicketTtl, load, ct);

    public async Task InvalidateTicketListsAsync(Guid customerId, Guid? agentId, CancellationToken ct)
    {
        await AdvanceVersionAsync(CustomerListVersionKey(customerId), ct);
        await AdvanceVersionAsync(AgentQueueVersionKey, ct);
        if (agentId is not null) await AdvanceVersionAsync(AgentVersionKey(agentId.Value), ct);
    }

    /// <summary>
    /// Used by the durable outbox. Unlike the request-path method, a cache outage is surfaced so
    /// the outbox record is retried; the originating database transaction has already committed.
    /// A delivery always writes a fresh opaque token: outbox events can be delivered out of order,
    /// so using their numeric IDs as a mutable version could resurrect a stale cache entry.
    /// </summary>
    public async Task InvalidateTicketListsFromOutboxAsync(Guid customerId, Guid? agentId, CancellationToken ct)
    {
        await AdvanceVersionStrictAsync(CustomerListVersionKey(customerId), ct);
        await AdvanceVersionStrictAsync(AgentQueueVersionKey, ct);
        if (agentId is not null) await AdvanceVersionStrictAsync(AgentVersionKey(agentId.Value), ct);
    }

    public Task InvalidateMessagesAsync(long ticketId, CancellationToken ct) => AdvanceVersionAsync(MessageVersionKey(ticketId), ct);

    private async Task<T> GetOrCreateAsync<T>(string key, string kind, DistributedCacheEntryOptions options,
        Func<CancellationToken, Task<T>> load, CancellationToken ct)
    {
        var cached = await TryGetAsync<T>(key, kind, ct);
        if (cached.Found) return cached.Value!;

        // This coalesces simultaneous local requests after a miss. The second cache read
        // means only the lock holder reaches the database while the cache is healthy.
        using (await AcquireMissLockAsync(key, ct))
        {
            cached = await TryGetAsync<T>(key, kind, ct);
            if (cached.Found) return cached.Value!;
            var value = await load(ct);
            await TrySetAsync(key, value, options, kind, ct);
            return value;
        }
    }

    private async Task<(bool Found, T? Value)> TryGetAsync<T>(string key, string kind, CancellationToken ct)
    {
        try
        {
            var bytes = await cache.GetAsync(key, ct);
            if (bytes is null)
            {
                Misses.Add(1, new KeyValuePair<string, object?>("kind", kind));
                return (false, default);
            }
            var value = JsonSerializer.Deserialize<T>(bytes, SerializerOptions);
            if (value is null) return (false, default);
            Hits.Add(1, new KeyValuePair<string, object?>("kind", kind));
            return (true, value);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Failures.Add(1, new KeyValuePair<string, object?>("operation", "get"));
            logger.LogWarning(exception, "Cache read failed for {CacheKind}; reading the database instead", kind);
            return (false, default);
        }
    }

    private async Task TrySetAsync<T>(string key, T value, DistributedCacheEntryOptions options, string kind, CancellationToken ct)
    {
        try
        {
            await cache.SetAsync(key, JsonSerializer.SerializeToUtf8Bytes(value, SerializerOptions), options, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Failures.Add(1, new KeyValuePair<string, object?>("operation", "set"));
            logger.LogWarning(exception, "Cache write failed for {CacheKind}; database result was returned", kind);
        }
    }

    private async Task<string> VersionAsync(string key, CancellationToken ct)
    {
        try
        {
            return await cache.GetStringAsync(key, ct) ?? "0";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Failures.Add(1, new KeyValuePair<string, object?>("operation", "version-read"));
            logger.LogWarning(exception, "Cache version read failed; bypassing cached data");
            return Guid.NewGuid().ToString("N");
        }
    }

    private async Task AdvanceVersionAsync(string key, CancellationToken ct)
    {
        try
        {
            await cache.SetStringAsync(key, Guid.NewGuid().ToString("N"), VersionTtl, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Failures.Add(1, new KeyValuePair<string, object?>("operation", "version-write"));
            logger.LogWarning(exception, "Cache invalidation failed; the database remains authoritative");
        }
    }

    private async Task AdvanceVersionStrictAsync(string key, CancellationToken ct) =>
        await cache.SetStringAsync(key, Guid.NewGuid().ToString("N"), VersionTtl, ct);

    private static async Task<MissLockLease> AcquireMissLockAsync(string key, CancellationToken ct)
    {
        while (true)
        {
            var entry = MissLocks.GetOrAdd(key, _ => new MissLock());
            Interlocked.Increment(ref entry.References);
            if (!MissLocks.TryGetValue(key, out var current) || !ReferenceEquals(entry, current))
            {
                ReleaseReference(key, entry);
                continue;
            }

            try
            {
                await entry.Gate.WaitAsync(ct);
                return new MissLockLease(key, entry);
            }
            catch
            {
                ReleaseReference(key, entry);
                throw;
            }
        }
    }

    private static void ReleaseReference(string key, MissLock entry)
    {
        if (Interlocked.Decrement(ref entry.References) == 0)
            ((ICollection<KeyValuePair<string, MissLock>>)MissLocks).Remove(new KeyValuePair<string, MissLock>(key, entry));
    }

    private sealed class MissLock
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public int References;
    }

    private sealed class MissLockLease(string key, MissLock entry) : IDisposable
    {
        public void Dispose()
        {
            entry.Gate.Release();
            ReleaseReference(key, entry);
        }
    }

    private static string CustomerListVersionKey(Guid customerId) => $"{Prefix}:versions:ticket-lists:customer:{customerId:N}";
    private const string AgentQueueVersionKey = Prefix + ":versions:ticket-lists:agent-queue";
    private static readonly DistributedCacheEntryOptions VersionTtl = new() { AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24) };
    private static string AgentVersionKey(Guid agentId) => $"{Prefix}:versions:agent:{agentId:N}";
    private static string MessageVersionKey(long ticketId) => $"{Prefix}:versions:ticket:{ticketId}:messages";
}
