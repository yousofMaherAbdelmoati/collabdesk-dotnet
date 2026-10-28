using CollabDesk.Application;
using CollabDesk.Domain;
using CollabDesk.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CollabDesk.IntegrationTests;

/// <summary>
/// PostgreSQL acceptance checks. Set COLLABDESK_TEST_POSTGRES to an isolated local database
/// named collabdesk_test_*; the fixture drops and recreates only that named test database.
/// </summary>
[Collection("postgresql")]
public sealed class ConcurrencySpecification : IAsyncLifetime
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
    public async Task Twenty_simultaneous_creations_with_the_same_key_replay_one_committed_ticket()
    {
        RequirePostgres();
        var userId = Guid.NewGuid();
        var key = $"parallel-create-{Guid.NewGuid():N}";
        var calls = Enumerable.Range(0, 20).Select(async _ =>
        {
            await using var db = CreateDb();
            return await new TicketService(db).CreateAsync(userId, key, new CreateTicket("Parallel", "Fictional concurrent ticket"), CancellationToken.None);
        });

        var results = await Task.WhenAll(calls);
        Assert.Single(results.Select(result => result.Value.Id).Distinct());
        Assert.Equal(1, results.Count(result => !result.Replay));
        Assert.Equal(19, results.Count(result => result.Replay));
        await using var verify = CreateDb();
        Assert.Equal(1, await verify.Tickets.CountAsync());
        Assert.Equal(1, await verify.Outbox.CountAsync());
    }

    [PostgreSqlFact]
    public async Task Twenty_simultaneous_claims_have_exactly_one_winner()
    {
        RequirePostgres();
        var ticketId = await AddOpenTicketAsync();
        var calls = Enumerable.Range(0, 20).Select(async _ =>
        {
            await using var db = CreateDb();
            return await new TicketService(db).ClaimAsync(ticketId, Guid.NewGuid(), CancellationToken.None);
        });

        var results = await Task.WhenAll(calls);
        Assert.Equal(1, results.Count(result => result.Result == ConditionalTicketResult.Updated));
        Assert.Equal(19, results.Count(result => result.Result == ConditionalTicketResult.Conflict));
        await using var verify = CreateDb();
        var ticket = await verify.Tickets.SingleAsync(ticket => ticket.Id == ticketId);
        Assert.Equal(TicketStatus.Claimed, ticket.Status);
        Assert.NotNull(ticket.AssignedAgentId);
    }

    [PostgreSqlFact]
    public async Task Twenty_simultaneous_chat_retries_persist_exactly_one_client_message()
    {
        RequirePostgres();
        var ticketId = await AddOpenTicketAsync();
        var clientMessageId = Guid.NewGuid();
        var senderId = Guid.NewGuid();
        var calls = Enumerable.Range(0, 20).Select(async _ =>
        {
            await using var db = CreateDb();
            db.Messages.Add(new ChatMessage
            {
                TicketId = ticketId,
                SenderId = senderId,
                ClientMessageId = clientMessageId,
                Body = "Fictional concurrent chat retry"
            });
            try
            {
                await db.SaveChangesAsync();
                return true;
            }
            catch (DbUpdateException)
            {
                return false;
            }
        });

        var results = await Task.WhenAll(calls);
        Assert.Equal(1, results.Count(saved => saved));
        await using var verify = CreateDb();
        Assert.Single(await verify.Messages.Where(message => message.TicketId == ticketId && message.ClientMessageId == clientMessageId).ToListAsync());
    }

    [PostgreSqlFact]
    public async Task Idempotency_mismatch_rolls_back_failed_work_and_a_retry_can_succeed()
    {
        RequirePostgres();
        var userId = Guid.NewGuid();
        var key = $"recovery-{Guid.NewGuid():N}";
        await using (var failedDb = CreateDb())
        {
            var failedService = new TicketService(failedDb);
            await Assert.ThrowsAnyAsync<DbUpdateException>(() => failedService.CreateAsync(
                userId, key, new CreateTicket(new string('x', 161), "This triggers the database length constraint"), CancellationToken.None));
        }

        await using (var verifyRollback = CreateDb())
        {
            Assert.Equal(0, await verifyRollback.Tickets.CountAsync());
            Assert.Equal(0, await verifyRollback.IdempotencyRecords.CountAsync());
            Assert.Equal(0, await verifyRollback.Outbox.CountAsync());
        }

        await using var retryDb = CreateDb();
        var retryService = new TicketService(retryDb);
        var first = await retryService.CreateAsync(userId, key, new CreateTicket("Retry", "Fictional retry"), CancellationToken.None);
        Assert.False(first.Replay);
        await Assert.ThrowsAsync<IdempotencyConflictException>(() => retryService.CreateAsync(
            userId, key, new CreateTicket("Different", "Different payload"), CancellationToken.None));
    }

    private CollabDeskDbContext CreateDb() => new(new DbContextOptionsBuilder<CollabDeskDbContext>()
        .UseNpgsql(_connectionString!).Options);

    private async Task<long> AddOpenTicketAsync()
    {
        await using var db = CreateDb();
        var ticket = new Ticket { CustomerId = Guid.NewGuid(), Title = "Claim me", Description = "Fictional race fixture" };
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync();
        return ticket.Id;
    }

    private void RequirePostgres() => ArgumentException.ThrowIfNullOrWhiteSpace(_connectionString);
}

[CollectionDefinition("postgresql", DisableParallelization = true)]
public sealed class PostgreSqlCollection;

public sealed class PostgreSqlFactAttribute : FactAttribute
{
    public PostgreSqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COLLABDESK_TEST_POSTGRES")))
            Skip = "Set COLLABDESK_TEST_POSTGRES to run PostgreSQL concurrency acceptance tests.";
    }
}
