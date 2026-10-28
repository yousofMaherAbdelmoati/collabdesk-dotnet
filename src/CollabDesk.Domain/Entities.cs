namespace CollabDesk.Domain;
public enum Role { Customer, Agent, Administrator }
public enum TicketStatus { Open, Claimed, Resolved }
public enum IdempotencyState { Processing, Completed }
public enum ReportStatus { Queued, Processing, Completed, Failed, Cancelled }
public enum OutboxStatus { Pending, Processing, Published, Failed }
public sealed class AppUser { public Guid Id { get; set; } = Guid.NewGuid(); public required string Email { get; set; } public required string NormalizedEmail { get; set; } public required string PasswordHash { get; set; } public Role Role { get; set; } }
public sealed class AuthSession { public Guid Id { get; set; } = Guid.NewGuid(); public Guid UserId { get; set; } public DateTimeOffset ExpiresAt { get; set; } public DateTimeOffset? RevokedAt { get; set; } public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow; }
public sealed class Ticket { public long Id { get; set; } public Guid CustomerId { get; set; } public Guid? AssignedAgentId { get; set; } public required string Title { get; set; } public required string Description { get; set; } public TicketStatus Status { get; set; } = TicketStatus.Open; public uint Version { get; set; } public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow; public DateTimeOffset? AssignedAt { get; set; } public DateTimeOffset? ResolvedAt { get; set; } }
public sealed class IdempotencyRecord { public long Id { get; set; } public Guid UserId { get; set; } public required string Operation { get; set; } public required string Key { get; set; } public required string Fingerprint { get; set; } public IdempotencyState State { get; set; } public int? StatusCode { get; set; } public string? ResponseJson { get; set; } public DateTimeOffset ExpiresAt { get; set; } }
public sealed class ChatMessage { public long Id { get; set; } public long TicketId { get; set; } public Guid SenderId { get; set; } public Guid ClientMessageId { get; set; } public required string Body { get; set; } public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow; public DateTimeOffset? ReadAt { get; set; } }
/// <summary>Each participant's durable read cursor for a ticket.</summary>
public sealed class ChatReadReceipt { public long Id { get; set; } public long TicketId { get; set; } public Guid ReaderId { get; set; } public long LastReadMessageId { get; set; } public DateTimeOffset ReadAt { get; set; } = DateTimeOffset.UtcNow; }
/// <summary>A durable report request. A lease token fences stale workers from completing work they no longer own.</summary>
public sealed class ReportJob
{
    public long Id { get; set; }
    public Guid RequestedById { get; set; }
    public required string Fingerprint { get; set; }
    /// <summary>Canonical, validated request used to reproduce this report after a restart.</summary>
    public required string RequestJson { get; set; }
    public ReportStatus Status { get; set; } = ReportStatus.Queued;
    public int Attempts { get; set; }
    public string? ResultJson { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? ResultExpiresAt { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Precomputed platform activity for one UTC day.</summary>
public sealed class DailyTicketStat
{
    public long Id { get; set; }
    public DateOnly Day { get; set; }
    public int CreatedTickets { get; set; }
    public int ClaimedTickets { get; set; }
    public int ResolvedTickets { get; set; }
    public long ResponseTimeTotalSeconds { get; set; }
    public int ResponseTimeCount { get; set; }
    public int ActivityCount { get; set; }
    public DateTimeOffset ComputedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Precomputed per-agent activity for one UTC day.</summary>
public sealed class DailyAgentStat
{
    public long Id { get; set; }
    public DateOnly Day { get; set; }
    public Guid AgentId { get; set; }
    public int ClaimedTickets { get; set; }
    public int ResolvedTickets { get; set; }
    public long ResponseTimeTotalSeconds { get; set; }
    public int ResponseTimeCount { get; set; }
    public int ActivityCount { get; set; }
    public DateTimeOffset ComputedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Precomputed platform activity for one UTC calendar month.</summary>
public sealed class MonthlyTicketStat
{
    public long Id { get; set; }
    public DateOnly Month { get; set; }
    public int CreatedTickets { get; set; }
    public int ClaimedTickets { get; set; }
    public int ResolvedTickets { get; set; }
    public long ResponseTimeTotalSeconds { get; set; }
    public int ResponseTimeCount { get; set; }
    public int ActivityCount { get; set; }
    public DateTimeOffset ComputedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class MonthlyAgentStat
{
    public long Id { get; set; }
    public DateOnly Month { get; set; }
    public Guid AgentId { get; set; }
    public int ClaimedTickets { get; set; }
    public int ResolvedTickets { get; set; }
    public long ResponseTimeTotalSeconds { get; set; }
    public int ResponseTimeCount { get; set; }
    public int ActivityCount { get; set; }
    public DateTimeOffset ComputedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A transactional outbox record. Publishing is at-least-once: consumers must use Id as their
/// idempotency key and must tolerate a successful side effect followed by a process crash.
/// </summary>
public sealed class OutboxEvent
{
    public long Id { get; set; }
    public required string Type { get; set; }
    public required string PayloadJson { get; set; }
    public OutboxStatus Status { get; set; } = OutboxStatus.Pending;
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? PublishedAt { get; set; }
}
