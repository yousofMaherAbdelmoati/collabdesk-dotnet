using System.Collections.Concurrent;
using CollabDesk.Application;
using CollabDesk.Domain;
using CollabDesk.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CollabDesk.IntegrationTests;

public sealed class TicketCacheSpecification
{
    [Fact]
    public async Task Ticket_reads_use_cache_then_invalidate_after_a_ticket_transition()
    {
        await using var fixture = await CacheFixture.CreateAsync();
        var customer = Guid.NewGuid();
        var agent = Guid.NewGuid();
        var ticket = await fixture.AddTicketAsync(customer, "First ticket");
        var service = fixture.CreateService();

        var first = await service.ListAsync(customer, Role.Customer, null, 1, 25, CancellationToken.None);
        await fixture.AddTicketAsync(customer, "Added after first read");
        var cached = await service.ListAsync(customer, Role.Customer, null, 1, 25, CancellationToken.None);
        Assert.Single(first.Items);
        Assert.Single(cached.Items); // The second read is served from the cache-aside value.

        var claim = await service.ClaimAsync(ticket.Id, agent, CancellationToken.None);
        Assert.Equal(ConditionalTicketResult.Updated, claim.Result);
        var invalidated = await service.ListAsync(customer, Role.Customer, null, 1, 25, CancellationToken.None);
        Assert.Equal(2, invalidated.Items.Count);
        Assert.Contains(invalidated.Items, item => item.Id == ticket.Id && item.Status == TicketStatus.Claimed.ToString());
        Assert.Contains(fixture.Cache!.Keys, key => key.StartsWith("collabdesk:v1:versions:ticket-lists:customer:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ticket_and_agent_cache_keys_are_identity_scoped_and_agent_stats_invalidate()
    {
        await using var fixture = await CacheFixture.CreateAsync();
        var customerA = Guid.NewGuid();
        var customerB = Guid.NewGuid();
        var agent = Guid.NewGuid();
        var first = await fixture.AddTicketAsync(customerA, "Customer A private ticket");
        await fixture.AddTicketAsync(customerB, "Customer B private ticket");
        var service = fixture.CreateService();

        var a = await service.ListAsync(customerA, Role.Customer, null, 1, 25, CancellationToken.None);
        var b = await service.ListAsync(customerB, Role.Customer, null, 1, 25, CancellationToken.None);
        Assert.Single(a.Items);
        Assert.Single(b.Items);
        Assert.NotEqual(a.Items[0].Id, b.Items[0].Id);
        Assert.Contains(fixture.Cache!.Keys, key => key.Contains($"user:{customerA:N}", StringComparison.Ordinal));
        Assert.Contains(fixture.Cache!.Keys, key => key.Contains($"user:{customerB:N}", StringComparison.Ordinal));

        var before = await service.GetAgentStatsAsync(agent, CancellationToken.None);
        Assert.Equal(2, before.AvailableOpenTickets);
        await service.ClaimAsync(first.Id, agent, CancellationToken.None);
        var after = await service.GetAgentStatsAsync(agent, CancellationToken.None);
        Assert.Equal(1, after.AvailableOpenTickets);
        Assert.Equal(1, after.ClaimedTickets);
        Assert.Contains(fixture.Cache!.Keys, key => key.StartsWith("collabdesk:v1:agent-stats:agent:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Message_cache_is_version_invalidated_after_a_new_message()
    {
        await using var fixture = await CacheFixture.CreateAsync();
        var customer = Guid.NewGuid();
        var ticket = await fixture.AddTicketAsync(customer, "Message ticket");
        await fixture.AddMessageAsync(ticket.Id, customer, "one");
        var service = fixture.CreateService();

        var first = await service.GetMessagesAsync(ticket.Id, 1, 25, CancellationToken.None);
        await fixture.AddMessageAsync(ticket.Id, customer, "two");
        var cached = await service.GetMessagesAsync(ticket.Id, 1, 25, CancellationToken.None);
        Assert.Single(first);
        Assert.Single(cached);

        await service.InvalidateMessagesAsync(ticket.Id, CancellationToken.None);
        var invalidated = await service.GetMessagesAsync(ticket.Id, 1, 25, CancellationToken.None);
        Assert.Equal(2, invalidated.Count);
        Assert.Contains(fixture.Cache!.Keys, key => key == $"collabdesk:v1:versions:ticket:{ticket.Id}:messages");
    }

    [Fact]
    public async Task Concurrent_cache_misses_are_coalesced_to_one_database_load()
    {
        var cache = new InMemoryDistributedCache();
        var readCache = new TicketReadCache(cache, NullLogger<TicketReadCache>.Instance);
        var loads = 0;
        var user = Guid.NewGuid();
        var work = Enumerable.Range(0, 20).Select(_ => readCache.GetTicketListAsync(user, "customer", "all", 1, 25,
            async _ =>
            {
                Interlocked.Increment(ref loads);
                await Task.Delay(25);
                return new[] { "database value" };
            }, CancellationToken.None));

        var results = await Task.WhenAll(work);
        Assert.Equal(1, loads);
        Assert.All(results, result => Assert.Equal("database value", Assert.Single(result)));
    }

    [Fact]
    public async Task Redis_outage_returns_database_results_without_throwing()
    {
        await using var fixture = await CacheFixture.CreateAsync(new ThrowingDistributedCache());
        var customer = Guid.NewGuid();
        await fixture.AddTicketAsync(customer, "Database fallback");

        var result = await fixture.CreateService().ListAsync(customer, Role.Customer, null, 1, 25, CancellationToken.None);
        Assert.Single(result.Items);
    }

    [RedisFact]
    public async Task Redis_backed_cache_serves_a_second_read_without_reloading()
    {
        var connection = Environment.GetEnvironmentVariable("COLLABDESK_TEST_REDIS")!;
        using var redis = new RedisCache(Options.Create(new RedisCacheOptions
        {
            Configuration = connection,
            InstanceName = "collabdesk-net006:"
        }));
        var readCache = new TicketReadCache(redis, NullLogger<TicketReadCache>.Instance);
        var loads = 0;
        var user = Guid.NewGuid();

        var first = await readCache.GetTicketListAsync(user, "customer", "all", 1, 25, _ =>
        {
            Interlocked.Increment(ref loads);
            return Task.FromResult(new[] { "fictional Redis result" });
        }, CancellationToken.None);
        var second = await readCache.GetTicketListAsync(user, "customer", "all", 1, 25, _ =>
        {
            Interlocked.Increment(ref loads);
            return Task.FromResult(new[] { "should not be loaded" });
        }, CancellationToken.None);

        Assert.Equal(1, loads);
        Assert.Equal(first, second);
    }

    private sealed class CacheFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<CollabDeskDbContext> _options;
        public InMemoryDistributedCache? Cache { get; }
        private readonly TicketReadCache _readCache;

        private CacheFixture(SqliteConnection connection, InMemoryDistributedCache? cache, TicketReadCache readCache)
        {
            _connection = connection;
            _options = new DbContextOptionsBuilder<CollabDeskDbContext>().UseSqlite(connection).Options;
            Cache = cache;
            _readCache = readCache;
        }

        public static async Task<CacheFixture> CreateAsync(IDistributedCache? cache = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var selectedCache = cache ?? new InMemoryDistributedCache();
            var fixture = new CacheFixture(connection, selectedCache as InMemoryDistributedCache,
                new TicketReadCache(selectedCache, NullLogger<TicketReadCache>.Instance));
            await using var db = fixture.CreateDb();
            await db.Database.EnsureCreatedAsync();
            return fixture;
        }

        public TicketService CreateService() => new(CreateDb(), _readCache);

        public async Task<Ticket> AddTicketAsync(Guid customerId, string title)
        {
            await using var db = CreateDb();
            var ticket = new Ticket { CustomerId = customerId, Title = title, Description = "Fictional cache fixture" };
            db.Tickets.Add(ticket);
            await db.SaveChangesAsync();
            return ticket;
        }

        public async Task AddMessageAsync(long ticketId, Guid senderId, string body)
        {
            await using var db = CreateDb();
            db.Messages.Add(new ChatMessage { TicketId = ticketId, SenderId = senderId, ClientMessageId = Guid.NewGuid(), Body = body });
            await db.SaveChangesAsync();
        }

        private CollabDeskDbContext CreateDb() => new(_options);
        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
    }

    private sealed class InMemoryDistributedCache : IDistributedCache
    {
        private readonly ConcurrentDictionary<string, byte[]> _values = new(StringComparer.Ordinal);
        public IReadOnlyCollection<string> Keys => _values.Keys.ToArray();
        public byte[]? Get(string key) => _values.TryGetValue(key, out var value) ? value : null;
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult(Get(key));
        public void Refresh(string key) { }
        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;
        public void Remove(string key) => _values.TryRemove(key, out _);
        public Task RemoveAsync(string key, CancellationToken token = default) { Remove(key); return Task.CompletedTask; }
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => _values[key] = value;
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            Set(key, value, options);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingDistributedCache : IDistributedCache
    {
        private static Exception Outage() => new InvalidOperationException("Fictional Redis outage");
        public byte[]? Get(string key) => throw Outage();
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromException<byte[]?>(Outage());
        public void Refresh(string key) => throw Outage();
        public Task RefreshAsync(string key, CancellationToken token = default) => Task.FromException(Outage());
        public void Remove(string key) => throw Outage();
        public Task RemoveAsync(string key, CancellationToken token = default) => Task.FromException(Outage());
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw Outage();
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => Task.FromException(Outage());
    }
}

public sealed class RedisFactAttribute : FactAttribute
{
    public RedisFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COLLABDESK_TEST_REDIS")))
            Skip = "Set COLLABDESK_TEST_REDIS to run the local Redis cache acceptance test.";
    }
}
