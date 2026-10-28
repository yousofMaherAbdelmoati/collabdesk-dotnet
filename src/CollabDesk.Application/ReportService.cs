using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CollabDesk.Domain;
using CollabDesk.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CollabDesk.Application;

public sealed record ReportDefinition(string Type, DateOnly From, DateOnly To);
public sealed record ReportJobDto(long Id, string Status, string Type, DateOnly From, DateOnly To,
    DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, DateTimeOffset? ExpiresAt, string? Error);

/// <summary>
/// Creates immutable, fingerprint-addressed report jobs and computes their daily/monthly read model.
/// All periods are UTC calendar periods and both bounds are inclusive.
/// </summary>
public sealed class ReportService(CollabDeskDbContext db)
{
    public static readonly TimeSpan ResultTtl = TimeSpan.FromHours(1);

    public async Task<(ReportJob Job, bool Reused)> CreateOrRefreshAsync(Guid requestedById, ReportDefinition definition, bool refresh, CancellationToken ct)
    {
        var canonical = Canonicalize(definition);
        var fingerprint = Fingerprint(canonical);
        var now = DateTimeOffset.UtcNow;
        var existing = await db.ReportJobs.SingleOrDefaultAsync(item => item.Fingerprint == fingerprint, ct);
        if (existing is not null)
        {
            // A refresh never duplicates a job: it atomically turns a completed/failed/expired
            // record back into queued work. Processing and queued jobs remain the shared request.
            // A completed record without an expiry predates (or bypassed) the result-cache
            // policy. Treat it as expired: serving it indefinitely would make cache expiry
            // dependent on the history of the row.
            if (refresh || (existing.Status == ReportStatus.Completed &&
                (existing.ResultExpiresAt is null || existing.ResultExpiresAt <= now)) ||
                existing.Status is ReportStatus.Failed or ReportStatus.Cancelled)
            {
                await db.ReportJobs.Where(item => item.Id == existing.Id && item.Status != ReportStatus.Processing)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(item => item.Status, ReportStatus.Queued)
                        .SetProperty(item => item.ResultJson, (string?)null)
                        .SetProperty(item => item.Error, (string?)null)
                        .SetProperty(item => item.CompletedAt, (DateTimeOffset?)null)
                        .SetProperty(item => item.ResultExpiresAt, (DateTimeOffset?)null)
                        .SetProperty(item => item.NextAttemptAt, (DateTimeOffset?)now), ct);
                db.ChangeTracker.Clear();
                existing = await db.ReportJobs.SingleAsync(item => item.Id == existing.Id, ct);
            }
            return (existing, true);
        }

        var job = new ReportJob { RequestedById = requestedById, Fingerprint = fingerprint, RequestJson = canonical };
        db.ReportJobs.Add(job);
        try
        {
            await db.SaveChangesAsync(ct);
            return (job, false);
        }
        catch (DbUpdateException) // The database unique index is the cross-process deduplication lock.
        {
            db.ChangeTracker.Clear();
            var raced = await db.ReportJobs.SingleAsync(item => item.Fingerprint == fingerprint, ct);
            return (raced, true);
        }
    }

    public Task<ReportJob?> GetAsync(long id, CancellationToken ct) =>
        db.ReportJobs.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, ct);

    public async Task<bool> CancelAsync(long id, CancellationToken ct)
    {
        var changed = await db.ReportJobs.Where(item => item.Id == id && item.Status == ReportStatus.Queued)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, ReportStatus.Cancelled)
                .SetProperty(item => item.Error, "Cancelled by an administrator.")
                .SetProperty(item => item.NextAttemptAt, (DateTimeOffset?)null), ct);
        return changed == 1;
    }

    public async Task<string> GenerateAsync(ClaimedReportJob job, CancellationToken ct)
    {
        var definition = JsonSerializer.Deserialize<ReportDefinition>(job.RequestJson)
            ?? throw new InvalidOperationException("The report request is invalid.");
        await RebuildStatsAsync(definition.From, definition.To, ct);

        var dailyTickets = await db.DailyTicketStats.AsNoTracking().Where(item => item.Day >= definition.From && item.Day <= definition.To)
            .OrderBy(item => item.Day).ToListAsync(ct);
        var dailyAgents = await db.DailyAgentStats.AsNoTracking().Where(item => item.Day >= definition.From && item.Day <= definition.To)
            .OrderBy(item => item.Day).ThenBy(item => item.AgentId).ToListAsync(ct);
        var months = Months(definition.From, definition.To).ToArray();
        var monthlyTickets = await db.MonthlyTicketStats.AsNoTracking().Where(item => months.Contains(item.Month)).OrderBy(item => item.Month).ToListAsync(ct);
        var monthlyAgents = await db.MonthlyAgentStats.AsNoTracking().Where(item => months.Contains(item.Month)).OrderBy(item => item.Month).ThenBy(item => item.AgentId).ToListAsync(ct);

        object result = definition.Type switch
        {
            "tickets" => new { type = definition.Type, from = definition.From, to = definition.To, daily = dailyTickets, monthly = monthlyTickets },
            "agents" => new { type = definition.Type, from = definition.From, to = definition.To, daily = dailyAgents, monthly = monthlyAgents },
            _ => throw new InvalidOperationException("The report type is invalid.")
        };
        return JsonSerializer.Serialize(result);
    }

    /// <summary>
    /// Idempotently rebuilds the materialized daily and monthly counters for every calendar month
    /// touched by the requested dates. A monthly row is a calendar-month aggregate, so rebuilding
    /// only a partial report range must not leave that row with a partial-month total.
    /// </summary>
    public async Task RebuildStatsAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        from = new DateOnly(from.Year, from.Month, 1);
        to = new DateOnly(to.Year, to.Month, 1).AddMonths(1).AddDays(-1);
        // Distinct report requests can overlap. PostgreSQL advisory transaction locks serialize
        // each affected calendar month without forcing unrelated reports through one worker.
        // SQLite is only the local test provider and does not support advisory locks.
        var postgres = db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL";
        await using var transaction = postgres ? await db.Database.BeginTransactionAsync(ct) : null;
        if (postgres)
            foreach (var month in Months(from, to))
                await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", [AdvisoryLockKey(month)], ct);

        var fromInstant = AtUtc(from);
        var toExclusive = AtUtc(to.AddDays(1));
        // SQLite cannot translate DateTimeOffset comparisons. Production PostgreSQL keeps this
        // bounded at the database; the local integration provider evaluates the equivalent test fixture filter.
        var ticketQuery = db.Tickets.AsNoTracking().Where(ticket =>
            (ticket.CreatedAt >= fromInstant && ticket.CreatedAt < toExclusive) ||
            (ticket.AssignedAt >= fromInstant && ticket.AssignedAt < toExclusive) ||
            (ticket.ResolvedAt >= fromInstant && ticket.ResolvedAt < toExclusive));
        var messageQuery = db.Messages.AsNoTracking().Where(message => message.CreatedAt >= fromInstant && message.CreatedAt < toExclusive);
        var sqlite = db.Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite";
        var tickets = sqlite
            ? (await db.Tickets.AsNoTracking().ToListAsync(ct)).Where(ticket =>
                (ticket.CreatedAt >= fromInstant && ticket.CreatedAt < toExclusive) ||
                (ticket.AssignedAt >= fromInstant && ticket.AssignedAt < toExclusive) ||
                (ticket.ResolvedAt >= fromInstant && ticket.ResolvedAt < toExclusive)).ToList()
            : await ticketQuery.ToListAsync(ct);
        var messages = sqlite
            ? (await db.Messages.AsNoTracking().ToListAsync(ct)).Where(message => message.CreatedAt >= fromInstant && message.CreatedAt < toExclusive).ToList()
            : await messageQuery.ToListAsync(ct);
        var agents = await db.Users.AsNoTracking().Where(user => user.Role == Role.Agent).Select(user => user.Id).ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var existingDaily = (await db.DailyTicketStats.Where(item => item.Day >= from && item.Day <= to).ToListAsync(ct))
            .ToDictionary(item => item.Day);
        var existingDailyAgents = (await db.DailyAgentStats.Where(item => item.Day >= from && item.Day <= to).ToListAsync(ct))
            .GroupBy(item => item.Day).ToDictionary(group => group.Key, group => group.ToList());

        foreach (var day in Days(from, to))
        {
            var dayStart = AtUtc(day);
            var dayEnd = AtUtc(day.AddDays(1));
            var claims = tickets.Where(ticket => ticket.AssignedAt >= dayStart && ticket.AssignedAt < dayEnd).ToArray();
            var daily = existingDaily.GetValueOrDefault(day) ?? Add(new DailyTicketStat { Day = day });
            daily.CreatedTickets = tickets.Count(ticket => ticket.CreatedAt >= dayStart && ticket.CreatedAt < dayEnd);
            daily.ClaimedTickets = claims.Length;
            daily.ResolvedTickets = tickets.Count(ticket => ticket.ResolvedAt >= dayStart && ticket.ResolvedAt < dayEnd);
            daily.ResponseTimeCount = claims.Length;
            daily.ResponseTimeTotalSeconds = claims.Sum(ticket => Math.Max(0, (long)(ticket.AssignedAt!.Value - ticket.CreatedAt).TotalSeconds));
            daily.ActivityCount = messages.Count(message => message.CreatedAt >= dayStart && message.CreatedAt < dayEnd);
            daily.ComputedAt = now;

            var activeAgentIds = claims.Where(ticket => ticket.AssignedAgentId is not null).Select(ticket => ticket.AssignedAgentId!.Value)
                .Concat(tickets.Where(ticket => ticket.ResolvedAt >= dayStart && ticket.ResolvedAt < dayEnd && ticket.AssignedAgentId is not null).Select(ticket => ticket.AssignedAgentId!.Value))
                .Concat(messages.Where(message => message.CreatedAt >= dayStart && message.CreatedAt < dayEnd && agents.Contains(message.SenderId)).Select(message => message.SenderId)).Distinct();
            var activeAgentSet = activeAgentIds.ToHashSet();
            var existingAgents = existingDailyAgents.GetValueOrDefault(day) ?? [];
            foreach (var stale in existingAgents.Where(item => !activeAgentSet.Contains(item.AgentId))) db.Remove(stale);
            var existingByAgent = existingAgents.Where(item => activeAgentSet.Contains(item.AgentId)).ToDictionary(item => item.AgentId);
            foreach (var agentId in activeAgentSet)
            {
                var agentClaims = claims.Where(ticket => ticket.AssignedAgentId == agentId).ToArray();
                var stat = existingByAgent.GetValueOrDefault(agentId) ?? Add(new DailyAgentStat { Day = day, AgentId = agentId });
                stat.ClaimedTickets = agentClaims.Length;
                stat.ResolvedTickets = tickets.Count(ticket => ticket.ResolvedAt >= dayStart && ticket.ResolvedAt < dayEnd && ticket.AssignedAgentId == agentId);
                stat.ResponseTimeCount = agentClaims.Length;
                stat.ResponseTimeTotalSeconds = agentClaims.Sum(ticket => Math.Max(0, (long)(ticket.AssignedAt!.Value - ticket.CreatedAt).TotalSeconds));
                stat.ActivityCount = messages.Count(message => message.SenderId == agentId && message.CreatedAt >= dayStart && message.CreatedAt < dayEnd);
                stat.ComputedAt = now;
            }
        }
        await db.SaveChangesAsync(ct);

        foreach (var month in Months(from, to))
        {
            var next = month.AddMonths(1);
            var sourceDays = await db.DailyTicketStats.Where(item => item.Day >= month && item.Day < next).ToListAsync(ct);
            var stat = await db.MonthlyTicketStats.SingleOrDefaultAsync(item => item.Month == month, ct) ?? Add(new MonthlyTicketStat { Month = month });
            Copy(sourceDays, stat, now);
            var sourceAgents = await db.DailyAgentStats.Where(item => item.Day >= month && item.Day < next).ToListAsync(ct);
            var groupedAgents = sourceAgents.GroupBy(item => item.AgentId).ToArray();
            var activeMonthlyAgents = groupedAgents.Select(group => group.Key).ToHashSet();
            var existingMonthlyAgents = await db.MonthlyAgentStats.Where(item => item.Month == month).ToListAsync(ct);
            foreach (var stale in existingMonthlyAgents.Where(item => !activeMonthlyAgents.Contains(item.AgentId))) db.Remove(stale);
            var existingMonthlyByAgent = existingMonthlyAgents.Where(item => activeMonthlyAgents.Contains(item.AgentId)).ToDictionary(item => item.AgentId);
            foreach (var group in groupedAgents)
            {
                var agent = existingMonthlyByAgent.GetValueOrDefault(group.Key) ?? Add(new MonthlyAgentStat { Month = month, AgentId = group.Key });
                Copy(group, agent, now);
            }
        }
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
    }

    private T Add<T>(T entity) where T : class { db.Add(entity); return entity; }
    private static void Copy(IEnumerable<DailyTicketStat> source, MonthlyTicketStat target, DateTimeOffset now)
    { var items = source.ToArray(); target.CreatedTickets = items.Sum(x => x.CreatedTickets); target.ClaimedTickets = items.Sum(x => x.ClaimedTickets); target.ResolvedTickets = items.Sum(x => x.ResolvedTickets); target.ResponseTimeTotalSeconds = items.Sum(x => x.ResponseTimeTotalSeconds); target.ResponseTimeCount = items.Sum(x => x.ResponseTimeCount); target.ActivityCount = items.Sum(x => x.ActivityCount); target.ComputedAt = now; }
    private static void Copy(IEnumerable<DailyAgentStat> source, MonthlyAgentStat target, DateTimeOffset now)
    { var items = source.ToArray(); target.ClaimedTickets = items.Sum(x => x.ClaimedTickets); target.ResolvedTickets = items.Sum(x => x.ResolvedTickets); target.ResponseTimeTotalSeconds = items.Sum(x => x.ResponseTimeTotalSeconds); target.ResponseTimeCount = items.Sum(x => x.ResponseTimeCount); target.ActivityCount = items.Sum(x => x.ActivityCount); target.ComputedAt = now; }
    public static ReportJobDto ToDto(ReportJob job) { var d = JsonSerializer.Deserialize<ReportDefinition>(job.RequestJson)!; return new(job.Id, job.Status.ToString(), d.Type, d.From, d.To, job.CreatedAt, job.CompletedAt, job.ResultExpiresAt, job.Error); }
    public static string Canonicalize(ReportDefinition definition) => JsonSerializer.Serialize(new ReportDefinition(definition.Type.Trim().ToLowerInvariant(), definition.From, definition.To));
    private static string Fingerprint(string canonical) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    private static DateTimeOffset AtUtc(DateOnly date) => new(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
    private static long AdvisoryLockKey(DateOnly month) => 0x4344_0000L + month.Year * 12L + month.Month;
    private static IEnumerable<DateOnly> Days(DateOnly from, DateOnly to) { for (var day = from; day <= to; day = day.AddDays(1)) yield return day; }
    private static IEnumerable<DateOnly> Months(DateOnly from, DateOnly to)
    {
        var last = new DateOnly(to.Year, to.Month, 1);
        for (var month = new DateOnly(from.Year, from.Month, 1); month <= last; month = month.AddMonths(1)) yield return month;
    }
}
