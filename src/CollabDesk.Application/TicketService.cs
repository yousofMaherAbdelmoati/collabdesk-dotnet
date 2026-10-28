using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CollabDesk.Domain;
using CollabDesk.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace CollabDesk.Application;

public sealed record CreateTicket(string Title, string Description);
public sealed record TicketDto(long Id, string Title, string Description, string Status, Guid CustomerId, Guid? AssignedAgentId, DateTimeOffset CreatedAt);
public sealed record TicketPageDto(IReadOnlyList<TicketDto> Items, int Page, int PageSize, int TotalCount);
public sealed record TicketMessageDto(long Id, Guid SenderId, Guid ClientMessageId, string Body, DateTimeOffset CreatedAt, long TicketId);
public sealed record ChatHistoryDto(IReadOnlyList<TicketMessageDto> Messages, long? NextAfterMessageId);
public sealed record AgentStatsDto(int AvailableOpenTickets, int ClaimedTickets, int ResolvedTickets);

public sealed class IdempotencyConflictException(string message) : Exception(message);
public sealed class IdempotencyInProgressException(string message) : Exception(message);

public enum ConditionalTicketResult { Updated, NotFound, Conflict, Forbidden }

/// <summary>Durable ticket operations backed by database transactions and conditional SQL.</summary>
public sealed class TicketService(CollabDeskDbContext db, TicketReadCache? readCache = null)
{
    private const string CreateOperation = "tickets.create";
    private const int IdempotencyRecoveryAttempts = 3;

    public async Task<(TicketDto Value, bool Replay)> CreateAsync(Guid userId, string key, CreateTicket input, CancellationToken ct)
    {
        var fingerprint = Hash(input);
        for (var attempt = 0; attempt < IdempotencyRecoveryAttempts; attempt++)
        {
            try
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                var record = new IdempotencyRecord
                {
                    UserId = userId, Operation = CreateOperation, Key = key, Fingerprint = fingerprint,
                    State = IdempotencyState.Processing, ExpiresAt = DateTimeOffset.UtcNow.AddHours(24)
                };
                // The unique index serializes duplicate keys across processes and connections.
                db.IdempotencyRecords.Add(record);
                await db.SaveChangesAsync(ct);

                var ticket = new Ticket { CustomerId = userId, Title = input.Title, Description = input.Description };
                db.Tickets.Add(ticket);
                await db.SaveChangesAsync(ct);

                var dto = ToDto(ticket);
                record.State = IdempotencyState.Completed;
                record.StatusCode = StatusCodes.Status201Created;
                record.ResponseJson = JsonSerializer.Serialize(dto);
                db.Outbox.Add(new OutboxEvent
                {
                    Type = "ticket.created", PayloadJson = JsonSerializer.Serialize(new { ticketId = ticket.Id, customerId = userId })
                });
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                if (readCache is not null) await readCache.InvalidateTicketListsAsync(userId, null, ct);
                return (dto, false);
            }
            catch (DbUpdateException exception) when (IsUniqueViolation(exception))
            {
                // A failed SaveChanges leaves Added entities tracked, so clear before recovery.
                db.ChangeTracker.Clear();
                var existing = await db.IdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(record =>
                    record.UserId == userId && record.Operation == CreateOperation && record.Key == key, ct);
                if (existing is null)
                    throw new InvalidOperationException("The idempotency record could not be recovered.", exception);
                if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
                    throw new IdempotencyConflictException("Idempotency-Key was reused with a different payload.");
                if (existing.State == IdempotencyState.Completed && existing.ResponseJson is not null)
                {
                    var replay = JsonSerializer.Deserialize<TicketDto>(existing.ResponseJson)
                        ?? throw new InvalidOperationException("The stored idempotency response is invalid.");
                    // A prior cache invalidation may have failed while Redis was unavailable.
                    // Replays are safe opportunities to repair that best-effort invalidation.
                    if (readCache is not null) await readCache.InvalidateTicketListsAsync(userId, null, ct);
                    return (replay, true);
                }

                // Transactions normally make Processing rows impossible after a crash. Recover
                // an expired legacy row atomically, without a process-local lock.
                if (existing.ExpiresAt <= DateTimeOffset.UtcNow)
                {
                    var removed = await db.IdempotencyRecords.Where(record => record.Id == existing.Id &&
                        record.State == IdempotencyState.Processing && record.ExpiresAt <= DateTimeOffset.UtcNow)
                        .ExecuteDeleteAsync(ct);
                    if (removed == 1) continue;
                }
                throw new IdempotencyInProgressException("The original request is still being processed; retry shortly.");
            }
            catch
            {
                db.ChangeTracker.Clear();
                throw;
            }
        }
        throw new IdempotencyInProgressException("The original request is still being processed; retry shortly.");
    }

    public async Task<TicketPageDto> ListAsync(Guid userId, Role role, TicketStatus? status, int page, int pageSize, CancellationToken ct)
    {
        async Task<TicketPageDto> LoadAsync(CancellationToken loadCt)
        {
            IQueryable<Ticket> tickets = db.Tickets.AsNoTracking();
            tickets = role switch
            {
                Role.Customer => tickets.Where(ticket => ticket.CustomerId == userId),
                // Agents may discover open work; assigned work is visible only to its owner.
                Role.Agent => tickets.Where(ticket => ticket.Status == TicketStatus.Open || ticket.AssignedAgentId == userId),
                Role.Administrator => tickets,
                _ => tickets.Where(_ => false)
            };
            if (status is not null) tickets = tickets.Where(ticket => ticket.Status == status);

            var totalCount = await tickets.CountAsync(loadCt);
            // SQLite cannot translate DateTimeOffset ordering. The production PostgreSQL query uses
            // CreatedAt/Id; the local API test provider uses the equivalent deterministic Id fallback.
            var orderedTickets = db.Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite"
                ? tickets.OrderByDescending(ticket => ticket.Id)
                : tickets.OrderByDescending(ticket => ticket.CreatedAt).ThenByDescending(ticket => ticket.Id);
            var items = await orderedTickets
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(ticket => new TicketDto(ticket.Id, ticket.Title, ticket.Description, ticket.Status.ToString(),
                    ticket.CustomerId, ticket.AssignedAgentId, ticket.CreatedAt)).ToListAsync(loadCt);
            return new TicketPageDto(items, page, pageSize, totalCount);
        }

        if (readCache is null || role == Role.Administrator) return await LoadAsync(ct);
        var statusKey = status?.ToString().ToLowerInvariant() ?? "all";
        return role == Role.Agent
            ? await readCache.GetAgentTicketListAsync(userId, statusKey, page, pageSize, LoadAsync, ct)
            : await readCache.GetTicketListAsync(userId, role.ToString().ToLowerInvariant(), statusKey, page, pageSize, LoadAsync, ct);
    }

    public async Task<(ConditionalTicketResult Result, TicketDto? Ticket)> ClaimAsync(long id, Guid agentId, CancellationToken ct)
    {
        var changed = await db.Tickets.Where(ticket => ticket.Id == id && ticket.Status == TicketStatus.Open && ticket.AssignedAgentId == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(ticket => ticket.Status, TicketStatus.Claimed)
                .SetProperty(ticket => ticket.AssignedAgentId, agentId)
                .SetProperty(ticket => ticket.AssignedAt, DateTimeOffset.UtcNow), ct);
        if (changed == 1)
        {
            var ticket = await db.Tickets.AsNoTracking().SingleAsync(ticket => ticket.Id == id, ct);
            if (readCache is not null) await readCache.InvalidateTicketListsAsync(ticket.CustomerId, agentId, ct);
            return (ConditionalTicketResult.Updated, ToDto(ticket));
        }
        return await db.Tickets.AsNoTracking().AnyAsync(ticket => ticket.Id == id, ct)
            ? (ConditionalTicketResult.Conflict, null) : (ConditionalTicketResult.NotFound, null);
    }

    public async Task<(ConditionalTicketResult Result, TicketDto? Ticket)> ResolveAsync(long id, Guid agentId, CancellationToken ct)
    {
        var changed = await db.Tickets.Where(ticket => ticket.Id == id && ticket.Status == TicketStatus.Claimed && ticket.AssignedAgentId == agentId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(ticket => ticket.Status, TicketStatus.Resolved)
                .SetProperty(ticket => ticket.ResolvedAt, DateTimeOffset.UtcNow), ct);
        if (changed == 1)
        {
            var ticket = await db.Tickets.AsNoTracking().SingleAsync(ticket => ticket.Id == id, ct);
            if (readCache is not null) await readCache.InvalidateTicketListsAsync(ticket.CustomerId, agentId, ct);
            return (ConditionalTicketResult.Updated, ToDto(ticket));
        }
        var current = await db.Tickets.AsNoTracking().SingleOrDefaultAsync(ticket => ticket.Id == id, ct);
        if (current is null) return (ConditionalTicketResult.NotFound, null);
        if (current.AssignedAgentId is not null && current.AssignedAgentId != agentId)
            return (ConditionalTicketResult.Forbidden, null);
        return (ConditionalTicketResult.Conflict, null);
    }

    public async Task<IReadOnlyList<TicketMessageDto>> GetMessagesAsync(long ticketId, int page, int pageSize, CancellationToken ct)
    {
        async Task<IReadOnlyList<TicketMessageDto>> LoadAsync(CancellationToken loadCt)
        {
            IQueryable<ChatMessage> messages = db.Messages.AsNoTracking().Where(message => message.TicketId == ticketId);
            var orderedMessages = db.Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite"
                ? messages.OrderByDescending(message => message.Id)
                : messages.OrderByDescending(message => message.CreatedAt).ThenByDescending(message => message.Id);
            return await orderedMessages
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(message => new TicketMessageDto(message.Id, message.SenderId, message.ClientMessageId, message.Body, message.CreatedAt, message.TicketId))
                .ToListAsync(loadCt);
        }

        return readCache is null ? await LoadAsync(ct) :
            await readCache.GetMessagesAsync(ticketId, "page", page, pageSize, LoadAsync, ct);
    }

    /// <summary>Returns the next stable, ascending slice after a persisted message ID.</summary>
    public async Task<ChatHistoryDto> GetMessagesAfterAsync(long ticketId, long afterMessageId, int pageSize, CancellationToken ct)
    {
        async Task<ChatHistoryDto> LoadAsync(CancellationToken loadCt)
        {
            var messages = await db.Messages.AsNoTracking()
                .Where(message => message.TicketId == ticketId && message.Id > afterMessageId)
                .OrderBy(message => message.Id)
                .Take(pageSize + 1)
                .Select(message => new TicketMessageDto(message.Id, message.SenderId, message.ClientMessageId, message.Body, message.CreatedAt, message.TicketId))
                .ToListAsync(loadCt);
            var hasMore = messages.Count > pageSize;
            if (hasMore) messages.RemoveAt(messages.Count - 1);
            return new ChatHistoryDto(messages, hasMore ? messages[^1].Id : null);
        }

        return readCache is null ? await LoadAsync(ct) :
            await readCache.GetMessagesAsync(ticketId, "after", afterMessageId, pageSize, LoadAsync, ct);
    }

    public async Task<AgentStatsDto> GetAgentStatsAsync(Guid agentId, CancellationToken ct)
    {
        async Task<AgentStatsDto> LoadAsync(CancellationToken loadCt)
        {
            var counts = await db.Tickets.AsNoTracking()
                .Where(ticket => ticket.Status == TicketStatus.Open || ticket.AssignedAgentId == agentId)
                .GroupBy(_ => 1)
                .Select(group => new
                {
                    Available = group.Count(ticket => ticket.Status == TicketStatus.Open),
                    Claimed = group.Count(ticket => ticket.Status == TicketStatus.Claimed && ticket.AssignedAgentId == agentId),
                    Resolved = group.Count(ticket => ticket.Status == TicketStatus.Resolved && ticket.AssignedAgentId == agentId)
                })
                .SingleOrDefaultAsync(loadCt);
            return counts is null ? new AgentStatsDto(0, 0, 0) : new AgentStatsDto(counts.Available, counts.Claimed, counts.Resolved);
        }

        return readCache is null ? await LoadAsync(ct) : await readCache.GetAgentStatsAsync(agentId, LoadAsync, ct);
    }

    public Task InvalidateMessagesAsync(long ticketId, CancellationToken ct) =>
        readCache?.InvalidateMessagesAsync(ticketId, ct) ?? Task.CompletedTask;

    public static TicketDto ToDto(Ticket ticket) => new(ticket.Id, ticket.Title, ticket.Description,
        ticket.Status.ToString(), ticket.CustomerId, ticket.AssignedAgentId, ticket.CreatedAt);

    private static string Hash(CreateTicket input) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(input))));

    private static bool IsUniqueViolation(DbUpdateException exception)
    {
        var inner = exception.InnerException;
        var sqlState = inner?.GetType().GetProperty("SqlState")?.GetValue(inner) as string;
        if (sqlState == "23505") return true; // PostgreSQL unique_violation
        return inner?.GetType().GetProperty("SqliteErrorCode")?.GetValue(inner) is int code && code == 19;
    }
}
