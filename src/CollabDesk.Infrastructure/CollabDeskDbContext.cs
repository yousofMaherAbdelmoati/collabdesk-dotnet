using CollabDesk.Domain;
using Microsoft.EntityFrameworkCore;

namespace CollabDesk.Infrastructure;

public sealed class CollabDeskDbContext(DbContextOptions<CollabDeskDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<AuthSession> AuthSessions => Set<AuthSession>();
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<ChatMessage> Messages => Set<ChatMessage>();
    public DbSet<ChatReadReceipt> ChatReadReceipts => Set<ChatReadReceipt>();
    public DbSet<ReportJob> ReportJobs => Set<ReportJob>();
    public DbSet<DailyTicketStat> DailyTicketStats => Set<DailyTicketStat>();
    public DbSet<DailyAgentStat> DailyAgentStats => Set<DailyAgentStat>();
    public DbSet<MonthlyTicketStat> MonthlyTicketStats => Set<MonthlyTicketStat>();
    public DbSet<MonthlyAgentStat> MonthlyAgentStats => Set<MonthlyAgentStat>();
    public DbSet<OutboxEvent> Outbox => Set<OutboxEvent>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<AppUser>().HasIndex(user => user.NormalizedEmail).IsUnique();
        builder.Entity<AuthSession>().HasIndex(session => new { session.UserId, session.ExpiresAt });

        var ticket = builder.Entity<Ticket>();
        var version = ticket.Property(item => item.Version).IsRowVersion();
        if (Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite") version.HasDefaultValue(0u);
        ticket.Property(item => item.Title).HasMaxLength(160);
        ticket.Property(item => item.Description).HasMaxLength(4000);
        // Customer pages, agent work queues, and deterministic keyset-compatible ordering.
        ticket.HasIndex(item => new { item.CustomerId, item.CreatedAt, item.Id });
        ticket.HasIndex(item => new { item.Status, item.AssignedAgentId, item.CreatedAt, item.Id });

        var idempotency = builder.Entity<IdempotencyRecord>();
        idempotency.Property(item => item.Key).HasMaxLength(200);
        idempotency.HasIndex(item => new { item.UserId, item.Operation, item.Key }).IsUnique();

        var message = builder.Entity<ChatMessage>();
        message.Property(item => item.Body).HasMaxLength(4000);
        message.HasIndex(item => new { item.TicketId, item.ClientMessageId }).IsUnique();
        message.HasIndex(item => new { item.TicketId, item.CreatedAt, item.Id });

        var receipt = builder.Entity<ChatReadReceipt>();
        receipt.HasIndex(item => new { item.TicketId, item.ReaderId }).IsUnique();
        receipt.HasIndex(item => new { item.TicketId, item.LastReadMessageId });

        var report = builder.Entity<ReportJob>();
        // The unique fingerprint makes concurrent identical requests converge on one durable job.
        report.HasIndex(job => job.Fingerprint).IsUnique();
        report.HasIndex(job => new { job.Status, job.NextAttemptAt, job.Id });

        var dailyTicket = builder.Entity<DailyTicketStat>();
        dailyTicket.HasIndex(item => item.Day).IsUnique();
        var dailyAgent = builder.Entity<DailyAgentStat>();
        dailyAgent.HasIndex(item => new { item.Day, item.AgentId }).IsUnique();
        var monthlyTicket = builder.Entity<MonthlyTicketStat>();
        monthlyTicket.HasIndex(item => item.Month).IsUnique();
        var monthlyAgent = builder.Entity<MonthlyAgentStat>();
        monthlyAgent.HasIndex(item => new { item.Month, item.AgentId }).IsUnique();

        var outbox = builder.Entity<OutboxEvent>();
        outbox.HasIndex(item => new { item.Status, item.NextAttemptAt, item.Id });
    }
}
