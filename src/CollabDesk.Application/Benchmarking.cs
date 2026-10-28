using System.Security.Cryptography;
using System.Text;
using CollabDesk.Domain;

namespace CollabDesk.Application;

/// <summary>
/// Deterministic, fictional fixture data and safety checks used by the local report benchmark.
/// This is deliberately separate from the Development initializer: benchmark data must never be
/// inserted into a normal development or deployment database.
/// </summary>
public static class ReportBenchmarkFixture
{
    public const string DatabasePrefix = "collabdesk_benchmark_";
    public const int DefaultSeed = 12012;
    public static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public const int ReportDays = 90;

    public static void RequireBenchmarkDatabase(string databaseName)
    {
        if (!databaseName.StartsWith(DatabasePrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Refusing to modify '{databaseName}'. Benchmark databases must begin with '{DatabasePrefix}'.");
    }

    public static void RequireLoopbackHost(string host)
    {
        if (host is not ("localhost" or "127.0.0.1" or "::1"))
            throw new InvalidOperationException("The benchmark accepts only a loopback PostgreSQL or Redis host.");
    }

    public static IReadOnlyList<AppUser> CreateUsers(int customerCount = 1000, int agentCount = 100)
    {
        var users = new List<AppUser>(customerCount + agentCount);
        for (var index = 0; index < customerCount; index++) users.Add(User("customer", index, Role.Customer));
        for (var index = 0; index < agentCount; index++) users.Add(User("agent", index, Role.Agent));
        return users;
    }

    public static IReadOnlyList<Ticket> CreateTickets(int count, int seed = DefaultSeed, int customerCount = 1000, int agentCount = 100)
    {
        if (count is not (10_000 or 50_000 or 100_000))
            throw new ArgumentOutOfRangeException(nameof(count), "Supported benchmark sizes are 10000, 50000, and 100000.");

        var random = new Random(seed);
        var tickets = new List<Ticket>(count);
        for (var index = 0; index < count; index++)
        {
            var created = Start.AddMinutes(random.Next(ReportDays * 24 * 60));
            var assigned = index % 5 == 0 ? (DateTimeOffset?)null : created.AddMinutes(1 + random.Next(720));
            var resolved = assigned is null || index % 3 == 0 ? (DateTimeOffset?)null : assigned.Value.AddMinutes(1 + random.Next(1440));
            tickets.Add(new Ticket
            {
                CustomerId = Id("customer", index % customerCount),
                AssignedAgentId = assigned is null ? null : Id("agent", index % agentCount),
                Title = $"Benchmark fictional ticket {index:D6}",
                Description = "Synthetic local-only benchmark fixture; not customer data.",
                Status = resolved is not null ? TicketStatus.Resolved : assigned is not null ? TicketStatus.Claimed : TicketStatus.Open,
                CreatedAt = created,
                AssignedAt = assigned,
                ResolvedAt = resolved
            });
        }
        return tickets;
    }

    private static AppUser User(string kind, int index, Role role) => new()
    {
        Id = Id(kind, index),
        Email = $"benchmark-{kind}-{index:D4}@fictional.test",
        NormalizedEmail = $"BENCHMARK-{kind.ToUpperInvariant()}-{index:D4}@FICTIONAL.TEST",
        PasswordHash = "benchmark-not-for-authentication",
        Role = role
    };

    private static Guid Id(string kind, int index) => new(SHA256.HashData(Encoding.UTF8.GetBytes($"collabdesk-benchmark:{kind}:{index}"))[..16]);
}

public sealed record DailyTicketBenchmarkRow(DateOnly Day, int CreatedTickets, int ClaimedTickets,
    int ResolvedTickets, long ResponseTimeTotalSeconds, int ResponseTimeCount, int ActivityCount);
