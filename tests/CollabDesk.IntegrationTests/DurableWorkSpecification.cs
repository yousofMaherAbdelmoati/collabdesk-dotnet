using CollabDesk.Api;
using CollabDesk.Application;
using CollabDesk.Domain;
using CollabDesk.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CollabDesk.IntegrationTests;

/// <summary>PostgreSQL-only checks exercise SKIP LOCKED and lease recovery using separate connections.</summary>
[Collection("postgresql")]
public sealed class DurableWorkPostgreSqlSpecification : IAsyncLifetime
{
    private readonly string? _connectionString = Environment.GetEnvironmentVariable("COLLABDESK_TEST_POSTGRES");

    public async Task InitializeAsync()
    {
        if (_connectionString is null) return;
        var database = new Npgsql.NpgsqlConnectionStringBuilder(_connectionString).Database;
        if (string.IsNullOrWhiteSpace(database) || !database.StartsWith("collabdesk_test_", StringComparison.Ordinal))
            throw new InvalidOperationException("COLLABDESK_TEST_POSTGRES must target an isolated collabdesk_test_* database.");
        await using var db = CreateDb();
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [PostgreSqlFact]
    public async Task Two_worker_instances_claim_a_report_only_once()
    {
        RequirePostgres();
        await using (var seed = CreateDb())
        {
            seed.ReportJobs.Add(NewReport());
            await seed.SaveChangesAsync();
        }

        var claims = Enumerable.Range(0, 20).Select(async _ =>
        {
            await using var db = CreateDb();
            return await new DurableWorkStore(db).ClaimReportAsync(Guid.NewGuid(), TimeSpan.FromMinutes(1), CancellationToken.None);
        });
        var results = await Task.WhenAll(claims);
        var claim = Assert.Single(results, item => item is not null)!;
        Assert.Equal(1, claim.Attempts);

        await using var verify = CreateDb();
        var job = await verify.ReportJobs.SingleAsync();
        Assert.Equal(ReportStatus.Processing, job.Status);
        Assert.Equal(1, job.Attempts);
    }

    [PostgreSqlFact]
    public async Task Concurrent_identical_report_requests_converge_on_one_fingerprint_job()
    {
        RequirePostgres();
        var definition = new ReportDefinition("tickets", new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 2));
        var requests = Enumerable.Range(0, 20).Select(async _ =>
        {
            await using var db = CreateDb();
            return await new ReportService(db).CreateOrRefreshAsync(Guid.NewGuid(), definition, false, CancellationToken.None);
        });
        var results = await Task.WhenAll(requests);
        Assert.Single(results.Select(result => result.Job.Id).Distinct());
        await using var verify = CreateDb();
        Assert.Single(await verify.ReportJobs.ToListAsync());
    }

    [PostgreSqlFact]
    public async Task Expired_worker_lease_is_recovered_and_completion_creates_outbox_in_the_same_transaction()
    {
        RequirePostgres();
        await using (var seed = CreateDb())
        {
            seed.ReportJobs.Add(new ReportJob
            {
                RequestedById = Guid.NewGuid(),
                Fingerprint = "fictional-restart",
                RequestJson = "{\"type\":\"tickets\",\"from\":\"2026-01-01\",\"to\":\"2026-01-01\"}",
                Status = ReportStatus.Processing,
                Attempts = 1,
                LeaseId = Guid.NewGuid(),
                LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            });
            await seed.SaveChangesAsync();
        }

        ClaimedReportJob claim;
        await using (var workerAfterRestart = CreateDb())
        {
            var store = new DurableWorkStore(workerAfterRestart);
            Assert.Equal(1, await store.RecoverExpiredReportLeasesAsync(CancellationToken.None));
            claim = Assert.IsType<ClaimedReportJob>(await store.ClaimReportAsync(Guid.NewGuid(), TimeSpan.FromMinutes(1), CancellationToken.None));
            Assert.Equal(2, claim.Attempts);
            Assert.True(await store.CompleteReportAsync(claim, "{\"fictional\":true}", CancellationToken.None));
        }

        await using var verify = CreateDb();
        var job = await verify.ReportJobs.SingleAsync();
        Assert.Equal(ReportStatus.Completed, job.Status);
        var published = await verify.Outbox.SingleAsync();
        Assert.Equal("report.completed", published.Type);
        Assert.Equal(OutboxStatus.Pending, published.Status);
    }

    [PostgreSqlFact]
    public async Task Lost_publish_ack_replays_the_same_event_id_and_only_one_publisher_claims_each_delivery()
    {
        RequirePostgres();
        long eventId;
        await using (var seed = CreateDb())
        {
            seed.Outbox.Add(new OutboxEvent { Type = "ticket.created", PayloadJson = "{\"ticketId\":1}" });
            await seed.SaveChangesAsync();
            eventId = await seed.Outbox.Select(item => item.Id).SingleAsync();
        }

        ClaimedOutboxEvent first;
        await using (var publisher = CreateDb())
        {
            var store = new DurableWorkStore(publisher);
            first = Assert.IsType<ClaimedOutboxEvent>(await store.ClaimOutboxAsync(Guid.NewGuid(), TimeSpan.FromMilliseconds(1), CancellationToken.None));
            // Simulate a side effect succeeding followed by process termination before its DB acknowledgement.
        }
        await Task.Delay(25);

        await using (var restartedPublisher = CreateDb())
        {
            var store = new DurableWorkStore(restartedPublisher);
            Assert.Equal(1, await store.RecoverExpiredOutboxLeasesAsync(CancellationToken.None));
            var replay = Assert.IsType<ClaimedOutboxEvent>(await store.ClaimOutboxAsync(Guid.NewGuid(), TimeSpan.FromMinutes(1), CancellationToken.None));
            Assert.Equal(eventId, first.Id);
            Assert.Equal(first.Id, replay.Id);
            Assert.Equal(2, replay.Attempts);
            Assert.True(await store.MarkOutboxPublishedAsync(replay, CancellationToken.None));
        }

        await using var verify = CreateDb();
        var item = await verify.Outbox.SingleAsync();
        Assert.Equal(OutboxStatus.Published, item.Status);
        Assert.NotNull(item.PublishedAt);
    }

    private static ReportJob NewReport() => new()
    {
        RequestedById = Guid.NewGuid(),
        Fingerprint = $"fictional-{Guid.NewGuid():N}",
        RequestJson = "{\"type\":\"tickets\",\"from\":\"2026-01-01\",\"to\":\"2026-01-01\"}"
    };

    private CollabDeskDbContext CreateDb() => new(new DbContextOptionsBuilder<CollabDeskDbContext>().UseNpgsql(_connectionString!).Options);
    private void RequirePostgres() => ArgumentException.ThrowIfNullOrWhiteSpace(_connectionString);
}

public sealed class DurableWorkFailureSpecification
{
    [Fact]
    public async Task Duplicate_ticket_created_delivery_is_idempotent_at_the_cache_side_effect()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<CollabDeskDbContext>().UseSqlite(connection).Options;
        await using var db = new CollabDeskDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var cache = new RecordingDistributedCache();
        var dispatcher = new CacheInvalidatingOutboxDispatcher(db, new TicketReadCache(cache, NullLogger<TicketReadCache>.Instance));
        var customerId = Guid.NewGuid();
        var @event = new OutboxEvent { Id = 42, Type = "ticket.created", PayloadJson = $"{{\"customerId\":\"{customerId}\"}}" };

        await dispatcher.DispatchAsync(@event, CancellationToken.None);
        var firstState = cache.Values.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        await dispatcher.DispatchAsync(@event, CancellationToken.None);

        Assert.All(cache.Values, item => Assert.NotEqual(firstState[item.Key], item.Value));
        Assert.All(cache.Values.Values, value => Assert.NotEqual("42", value));
    }

    [Fact]
    public async Task Redis_outage_leaves_the_outbox_event_retryable_and_bounded_retries_become_terminal()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<CollabDeskDbContext>().UseSqlite(connection).Options;
        await using var setup = new CollabDeskDbContext(options);
        await setup.Database.EnsureCreatedAsync();

        var customerId = Guid.NewGuid();
        setup.Outbox.Add(new OutboxEvent { Type = "ticket.created", PayloadJson = $"{{\"customerId\":\"{customerId}\"}}" });
        await setup.SaveChangesAsync();
        var eventId = await setup.Outbox.Select(item => item.Id).SingleAsync();

        await using var dispatchDb = new CollabDeskDbContext(options);
        var dispatcher = new CacheInvalidatingOutboxDispatcher(dispatchDb,
            new TicketReadCache(new ThrowingDistributedCache(), NullLogger<TicketReadCache>.Instance));
        await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.DispatchAsync(new OutboxEvent
        {
            Id = eventId,
            Type = "ticket.created",
            PayloadJson = $"{{\"customerId\":\"{customerId}\"}}"
        }, CancellationToken.None));

        var leaseId = Guid.NewGuid();
        await setup.Outbox.Where(item => item.Id == eventId).ExecuteUpdateAsync(setters => setters
            .SetProperty(item => item.Status, OutboxStatus.Processing)
            .SetProperty(item => item.LeaseId, leaseId)
            .SetProperty(item => item.LeaseExpiresAt, DateTimeOffset.UtcNow.AddMinutes(1)));
        var store = new DurableWorkStore(setup);
        Assert.True(await store.FailOutboxAsync(new ClaimedOutboxEvent(eventId, "ticket.created", "{}", 1, leaseId, DateTimeOffset.UtcNow.AddMinutes(1)),
            new InvalidOperationException("Fictional Redis outage"), CancellationToken.None));
        setup.ChangeTracker.Clear();
        var retryable = await setup.Outbox.SingleAsync();
        Assert.Equal(OutboxStatus.Pending, retryable.Status);
        Assert.NotNull(retryable.NextAttemptAt);

        var terminalLease = Guid.NewGuid();
        await setup.Outbox.Where(item => item.Id == eventId).ExecuteUpdateAsync(setters => setters
            .SetProperty(item => item.Status, OutboxStatus.Processing)
            .SetProperty(item => item.LeaseId, terminalLease));
        Assert.True(await store.FailOutboxAsync(new ClaimedOutboxEvent(eventId, "ticket.created", "{}", DurableWorkStore.MaxAttempts, terminalLease,
            DateTimeOffset.UtcNow.AddMinutes(1)), new InvalidOperationException("Fictional repeated outage"), CancellationToken.None));
        setup.ChangeTracker.Clear();
        Assert.Equal(OutboxStatus.Failed, (await setup.Outbox.SingleAsync()).Status);
    }

    private sealed class ThrowingDistributedCache : IDistributedCache
    {
        private static InvalidOperationException Outage() => new("Fictional Redis outage");
        public byte[]? Get(string key) => throw Outage();
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromException<byte[]?>(Outage());
        public void Refresh(string key) => throw Outage();
        public Task RefreshAsync(string key, CancellationToken token = default) => Task.FromException(Outage());
        public void Remove(string key) => throw Outage();
        public Task RemoveAsync(string key, CancellationToken token = default) => Task.FromException(Outage());
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw Outage();
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => Task.FromException(Outage());
    }

    private sealed class RecordingDistributedCache : IDistributedCache
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
        public byte[]? Get(string key) => null;
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult<byte[]?>(null);
        public void Refresh(string key) { }
        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;
        public void Remove(string key) => Values.Remove(key);
        public Task RemoveAsync(string key, CancellationToken token = default) { Remove(key); return Task.CompletedTask; }
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => Values[key] = System.Text.Encoding.UTF8.GetString(value);
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            Set(key, value, options);
            return Task.CompletedTask;
        }
    }
}
