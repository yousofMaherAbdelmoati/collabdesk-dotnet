using System.Data;
using System.Data.Common;
using CollabDesk.Domain;
using Microsoft.EntityFrameworkCore;

namespace CollabDesk.Infrastructure;

/// <summary>
/// PostgreSQL coordination for durable work. Claims use row locks only while selecting a single
/// candidate; the lease, not an open transaction, protects the actual work.
/// </summary>
public sealed class DurableWorkStore(CollabDeskDbContext db)
{
    public const int MaxAttempts = 5;

    public async Task<ClaimedReportJob?> ClaimReportAsync(Guid leaseId, TimeSpan leaseDuration, CancellationToken ct)
    {
        const string sql = """
            WITH candidate AS (
                SELECT "Id" FROM "ReportJobs"
                WHERE "Status" = 0 AND ("NextAttemptAt" IS NULL OR "NextAttemptAt" <= NOW())
                ORDER BY "Id"
                FOR UPDATE SKIP LOCKED
                LIMIT 1
            )
            UPDATE "ReportJobs" AS job
            SET "Status" = 1,
                "Attempts" = job."Attempts" + 1,
                "LeaseId" = @leaseId,
                "LeaseExpiresAt" = NOW() + @leaseDuration,
                "NextAttemptAt" = NULL,
                "Error" = NULL
            FROM candidate
            WHERE job."Id" = candidate."Id"
            RETURNING job."Id", job."RequestedById", job."Fingerprint", job."RequestJson", job."Attempts", job."LeaseExpiresAt";
            """;

        return await QuerySingleAsync(sql, leaseId, leaseDuration, reader => new ClaimedReportJob(
            reader.GetInt64(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4), leaseId,
            reader.GetFieldValue<DateTimeOffset>(5)), ct);
    }

    public async Task<bool> RenewReportLeaseAsync(long jobId, Guid leaseId, TimeSpan leaseDuration, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var changed = await db.ReportJobs.Where(job => job.Id == jobId && job.Status == ReportStatus.Processing &&
                job.LeaseId == leaseId && job.LeaseExpiresAt > now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.LeaseExpiresAt, now.Add(leaseDuration)), ct);
        return changed == 1;
    }

    public async Task<bool> CompleteReportAsync(ClaimedReportJob job, string resultJson, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var changed = await db.ReportJobs.Where(item => item.Id == job.Id && item.Status == ReportStatus.Processing &&
                item.LeaseId == job.LeaseId && item.LeaseExpiresAt > now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, ReportStatus.Completed)
                .SetProperty(item => item.ResultJson, resultJson)
                .SetProperty(item => item.Error, (string?)null)
                .SetProperty(item => item.CompletedAt, now)
                // Result-cache policy is applied at the durable completion boundary so a
                // restart cannot produce completed reports without an expiration.
                .SetProperty(item => item.ResultExpiresAt, now.AddHours(1))
                .SetProperty(item => item.LeaseId, (Guid?)null)
                .SetProperty(item => item.LeaseExpiresAt, (DateTimeOffset?)null), ct);
        if (changed == 0)
        {
            await transaction.RollbackAsync(ct);
            return false;
        }

        db.Outbox.Add(new OutboxEvent
        {
            Type = "report.completed",
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new { reportJobId = job.Id })
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<bool> FailReportAsync(ClaimedReportJob job, Exception exception, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var terminal = job.Attempts >= MaxAttempts;
        var changed = await db.ReportJobs.Where(item => item.Id == job.Id && item.Status == ReportStatus.Processing &&
                item.LeaseId == job.LeaseId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, terminal ? ReportStatus.Failed : ReportStatus.Queued)
                .SetProperty(item => item.Error, Truncate(exception.Message))
                .SetProperty(item => item.LeaseId, (Guid?)null)
                .SetProperty(item => item.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(item => item.NextAttemptAt, terminal ? (DateTimeOffset?)null : now.Add(Backoff(job.Attempts))), ct);
        return changed == 1;
    }

    public Task<int> RecoverExpiredReportLeasesAsync(CancellationToken ct) => ExecuteAsync(
        """
        UPDATE "ReportJobs"
        SET "Status" = CASE WHEN "Attempts" >= 5 THEN 3 ELSE 0 END,
            "Error" = 'Worker lease expired before completion.',
            "LeaseId" = NULL,
            "LeaseExpiresAt" = NULL,
            "NextAttemptAt" = CASE WHEN "Attempts" >= 5 THEN NULL ELSE NOW() END
        WHERE "Status" = 1 AND "LeaseExpiresAt" <= NOW();
        """, ct);

    public async Task<ClaimedOutboxEvent?> ClaimOutboxAsync(Guid leaseId, TimeSpan leaseDuration, CancellationToken ct)
    {
        const string sql = """
            WITH candidate AS (
                SELECT "Id" FROM "Outbox"
                WHERE "Status" = 0 AND ("NextAttemptAt" IS NULL OR "NextAttemptAt" <= NOW())
                ORDER BY "Id"
                FOR UPDATE SKIP LOCKED
                LIMIT 1
            )
            UPDATE "Outbox" AS item
            SET "Status" = 1,
                "Attempts" = item."Attempts" + 1,
                "LeaseId" = @leaseId,
                "LeaseExpiresAt" = NOW() + @leaseDuration,
                "NextAttemptAt" = NULL,
                "LastError" = NULL
            FROM candidate
            WHERE item."Id" = candidate."Id"
            RETURNING item."Id", item."Type", item."PayloadJson", item."Attempts", item."LeaseExpiresAt";
            """;

        return await QuerySingleAsync(sql, leaseId, leaseDuration, reader => new ClaimedOutboxEvent(
            reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), leaseId,
            reader.GetFieldValue<DateTimeOffset>(4)), ct);
    }

    public async Task<bool> MarkOutboxPublishedAsync(ClaimedOutboxEvent item, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var changed = await db.Outbox.Where(eventItem => eventItem.Id == item.Id && eventItem.Status == OutboxStatus.Processing &&
                eventItem.LeaseId == item.LeaseId && eventItem.LeaseExpiresAt > now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(eventItem => eventItem.Status, OutboxStatus.Published)
                .SetProperty(eventItem => eventItem.PublishedAt, now)
                .SetProperty(eventItem => eventItem.LeaseId, (Guid?)null)
                .SetProperty(eventItem => eventItem.LeaseExpiresAt, (DateTimeOffset?)null), ct);
        return changed == 1;
    }

    public async Task<bool> FailOutboxAsync(ClaimedOutboxEvent item, Exception exception, CancellationToken ct)
    {
        var terminal = item.Attempts >= MaxAttempts;
        var now = DateTimeOffset.UtcNow;
        var changed = await db.Outbox.Where(eventItem => eventItem.Id == item.Id && eventItem.Status == OutboxStatus.Processing &&
                eventItem.LeaseId == item.LeaseId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(eventItem => eventItem.Status, terminal ? OutboxStatus.Failed : OutboxStatus.Pending)
                .SetProperty(eventItem => eventItem.LastError, Truncate(exception.Message))
                .SetProperty(eventItem => eventItem.LeaseId, (Guid?)null)
                .SetProperty(eventItem => eventItem.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(eventItem => eventItem.NextAttemptAt, terminal ? (DateTimeOffset?)null : now.Add(Backoff(item.Attempts))), ct);
        return changed == 1;
    }

    public Task<int> RecoverExpiredOutboxLeasesAsync(CancellationToken ct) => ExecuteAsync(
        """
        UPDATE "Outbox"
        SET "Status" = CASE WHEN "Attempts" >= 5 THEN 3 ELSE 0 END,
            "LastError" = 'Publisher lease expired before acknowledgement.',
            "LeaseId" = NULL,
            "LeaseExpiresAt" = NULL,
            "NextAttemptAt" = CASE WHEN "Attempts" >= 5 THEN NULL ELSE NOW() END
        WHERE "Status" = 1 AND "LeaseExpiresAt" <= NOW();
        """, ct);

    private async Task<T?> QuerySingleAsync<T>(string sql, Guid leaseId, TimeSpan leaseDuration, Func<DbDataReader, T> materialize,
        CancellationToken ct) where T : class
    {
        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere) await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            AddParameter(command, "leaseId", leaseId);
            AddParameter(command, "leaseDuration", leaseDuration);
            await using var reader = await command.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct) ? materialize(reader) : null;
        }
        finally
        {
            if (openedHere) await connection.CloseAsync();
        }
    }

    private async Task<int> ExecuteAsync(string sql, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere) await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return await command.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            if (openedHere) await connection.CloseAsync();
        }
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    public static TimeSpan Backoff(int attempts) => TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(attempts, 6))));
    private static string Truncate(string message) => message.Length <= 2_000 ? message : message[..2_000];
}

public sealed record ClaimedReportJob(long Id, Guid RequestedById, string Fingerprint, string RequestJson, int Attempts, Guid LeaseId, DateTimeOffset LeaseExpiresAt);
public sealed record ClaimedOutboxEvent(long Id, string Type, string PayloadJson, int Attempts, Guid LeaseId, DateTimeOffset LeaseExpiresAt);
