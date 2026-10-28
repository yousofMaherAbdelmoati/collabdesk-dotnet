using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using CollabDesk.Application;
using CollabDesk.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using StackExchange.Redis;

var options = BenchmarkOptions.Parse(args);
var connectionString = Environment.GetEnvironmentVariable("COLLABDESK_BENCHMARK_POSTGRES")
    ?? throw new InvalidOperationException("Set COLLABDESK_BENCHMARK_POSTGRES to an isolated local PostgreSQL benchmark database.");
var connection = new NpgsqlConnectionStringBuilder(connectionString);
ReportBenchmarkFixture.RequireLoopbackHost(connection.Host ?? "");
var database = connection.Database
    ?? throw new InvalidOperationException("COLLABDESK_BENCHMARK_POSTGRES must include Database.");
ReportBenchmarkFixture.RequireBenchmarkDatabase(database);

var runner = new BenchmarkRunner(connectionString, options);
if (options.Command == "seed") await runner.SeedAsync();
else await runner.RunAsync(Environment.GetEnvironmentVariable("COLLABDESK_BENCHMARK_REDIS")
    ?? throw new InvalidOperationException("Set COLLABDESK_BENCHMARK_REDIS to the isolated local Redis endpoint."));

internal sealed record BenchmarkOptions(string Command, int Size, int Seed, int Warmup, int Iterations, string ResultsPath)
{
    public static BenchmarkOptions Parse(string[] args)
    {
        var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "";
        if (command is not ("seed" or "run")) throw new ArgumentException("Usage: seed|run --size 10000|50000|100000 [--seed 12012] [--warmup 3] [--iterations 15] [--results path]");
        var values = args.Skip(1).Chunk(2).ToDictionary(pair => pair[0], pair => pair.Length == 2 ? pair[1] : throw new ArgumentException($"Missing value for {pair[0]}"), StringComparer.OrdinalIgnoreCase);
        static int Number(IReadOnlyDictionary<string, string> v, string name, int fallback) => v.TryGetValue(name, out var text) && int.TryParse(text, out var value) ? value : fallback;
        var size = Number(values, "--size", 0);
        if (size is not (10_000 or 50_000 or 100_000)) throw new ArgumentException("--size must be 10000, 50000, or 100000.");
        var warmup = Number(values, "--warmup", 3);
        var iterations = Number(values, "--iterations", 15);
        if (warmup < 0 || iterations < 1) throw new ArgumentException("--warmup must be >= 0 and --iterations must be >= 1.");
        return new(command, size, Number(values, "--seed", ReportBenchmarkFixture.DefaultSeed), warmup, iterations,
            values.GetValueOrDefault("--results") ?? Path.Combine("docs", "benchmarks", $"net-012-{size}-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}.json"));
    }
}

internal sealed class BenchmarkRunner(string connectionString, BenchmarkOptions options)
{
    private static readonly DateOnly From = DateOnly.FromDateTime(ReportBenchmarkFixture.Start.UtcDateTime);
    private static readonly DateOnly To = From.AddDays(ReportBenchmarkFixture.ReportDays - 1);

    public async Task SeedAsync()
    {
        await using var db = CreateDb();
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE \"AuthSessions\", \"ChatReadReceipts\", \"DailyAgentStats\", \"DailyTicketStats\", \"IdempotencyRecords\", \"Messages\", \"MonthlyAgentStats\", \"MonthlyTicketStats\", \"Outbox\", \"ReportJobs\", \"Tickets\", \"Users\" RESTART IDENTITY CASCADE;");
        db.Users.AddRange(ReportBenchmarkFixture.CreateUsers());
        await db.SaveChangesAsync();
        foreach (var batch in ReportBenchmarkFixture.CreateTickets(options.Size, options.Seed).Chunk(1000))
        {
            db.Tickets.AddRange(batch);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }
        var count = await db.Tickets.CountAsync();
        if (count != options.Size) throw new InvalidOperationException($"Seed verification failed: expected {options.Size}, found {count}.");
        Console.WriteLine($"Seeded {count} deterministic fictional tickets in isolated benchmark database.");
    }

    public async Task RunAsync(string redisConnection)
    {
        var redisOptions = ConfigurationOptions.Parse(redisConnection);
        foreach (var endpoint in redisOptions.EndPoints.OfType<DnsEndPoint>()) ReportBenchmarkFixture.RequireLoopbackHost(endpoint.Host);
        await using (var verify = CreateDb())
        {
            if (await verify.Tickets.CountAsync() != options.Size)
                throw new InvalidOperationException($"Run 'seed --size {options.Size}' first; the isolated database does not contain that exact fixture size.");
        }

        var precompute = Stopwatch.StartNew();
        await using (var db = CreateDb()) await new ReportService(db).RebuildStatsAsync(From, To, CancellationToken.None);
        precompute.Stop();
        var expected = await OnDemandAsync(new QueryCounter());
        await using var redis = await ConnectionMultiplexer.ConnectAsync(redisOptions);
        var cache = redis.GetDatabase();
        var cacheKey = $"collabdesk:benchmark:net-012:{options.Size}:{options.Seed}:ticket-report:{From:yyyyMMdd}:{To:yyyyMMdd}";
        await cache.KeyDeleteAsync(cacheKey);
        await cache.StringSetAsync(cacheKey, JsonSerializer.Serialize(expected), TimeSpan.FromHours(1));

        var measurements = new List<Measurement>();
        foreach (var strategy in new[] { "on-demand-ef", "precomputed", "redis-cached" })
        {
            for (var index = 0; index < options.Warmup + options.Iterations; index++)
            {
                var counter = new QueryCounter();
                var timer = Stopwatch.StartNew();
                var actual = strategy switch
                {
                    "on-demand-ef" => await OnDemandAsync(counter),
                    "precomputed" => await PrecomputedAsync(counter),
                    _ => JsonSerializer.Deserialize<List<DailyTicketBenchmarkRow>>((string?)await cache.StringGetAsync(cacheKey) ?? throw new InvalidOperationException("Redis cache entry disappeared."))!
                };
                timer.Stop();
                if (!actual.SequenceEqual(expected)) throw new InvalidOperationException($"{strategy} did not return the same report rows as the on-demand EF aggregation.");
                measurements.Add(new(strategy, index < options.Warmup ? "warmup" : "measurement", index, timer.Elapsed.TotalMilliseconds, counter.Count));
            }
        }
        string serverVersion;
        await using (var environmentDb = CreateDb()) serverVersion = await environmentDb.Database.GetDbConnection().OpenAndVersionAsync();
        var report = new BenchmarkReport("NET-012", DateTimeOffset.UtcNow, options.Size, options.Seed, options.Warmup, options.Iterations, From, To,
            Environment.Version.ToString(), System.Runtime.InteropServices.RuntimeInformation.OSDescription, serverVersion, redis.GetServer(redis.GetEndPoints().Single()).Version.ToString(),
            precompute.Elapsed.TotalMilliseconds, measurements);
        Directory.CreateDirectory(Path.GetDirectoryName(options.ResultsPath) ?? ".");
        await File.WriteAllTextAsync(options.ResultsPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Saved {measurements.Count} raw measurements to {options.ResultsPath}");
    }

    private CollabDeskDbContext CreateDb(QueryCounter? counter = null)
    {
        var builder = new DbContextOptionsBuilder<CollabDeskDbContext>().UseNpgsql(connectionString);
        if (counter is not null) builder.AddInterceptors(counter);
        return new CollabDeskDbContext(builder.Options);
    }

    private async Task<List<DailyTicketBenchmarkRow>> OnDemandAsync(QueryCounter counter)
    {
        await using var db = CreateDb(counter);
        var start = new DateTimeOffset(From.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        var end = new DateTimeOffset(To.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        var created = await db.Tickets.AsNoTracking().Where(x => x.CreatedAt >= start && x.CreatedAt < end).GroupBy(x => x.CreatedAt.Date).Select(g => new { Day = g.Key, Count = g.Count() }).ToListAsync();
        var claimed = await db.Tickets.AsNoTracking().Where(x => x.AssignedAt >= start && x.AssignedAt < end).GroupBy(x => x.AssignedAt!.Value.Date).Select(g => new { Day = g.Key, Count = g.Count(), Seconds = g.Sum(x => (long)(x.AssignedAt!.Value - x.CreatedAt).TotalSeconds) }).ToListAsync();
        var resolved = await db.Tickets.AsNoTracking().Where(x => x.ResolvedAt >= start && x.ResolvedAt < end).GroupBy(x => x.ResolvedAt!.Value.Date).Select(g => new { Day = g.Key, Count = g.Count() }).ToListAsync();
        var values = Enumerable.Range(0, ReportBenchmarkFixture.ReportDays).Select(index => From.AddDays(index)).ToDictionary(day => day, day => new DailyTicketBenchmarkRow(day, 0, 0, 0, 0, 0, 0));
        foreach (var item in created) { var day = DateOnly.FromDateTime(item.Day); values[day] = values[day] with { CreatedTickets = item.Count }; }
        foreach (var item in claimed) { var day = DateOnly.FromDateTime(item.Day); values[day] = values[day] with { ClaimedTickets = item.Count, ResponseTimeCount = item.Count, ResponseTimeTotalSeconds = item.Seconds }; }
        foreach (var item in resolved) { var day = DateOnly.FromDateTime(item.Day); values[day] = values[day] with { ResolvedTickets = item.Count }; }
        return values.Values.OrderBy(x => x.Day).ToList();
    }

    private async Task<List<DailyTicketBenchmarkRow>> PrecomputedAsync(QueryCounter counter)
    {
        await using var db = CreateDb(counter);
        return await db.DailyTicketStats.AsNoTracking().Where(x => x.Day >= From && x.Day <= To).OrderBy(x => x.Day)
            .Select(x => new DailyTicketBenchmarkRow(x.Day, x.CreatedTickets, x.ClaimedTickets, x.ResolvedTickets, x.ResponseTimeTotalSeconds, x.ResponseTimeCount, x.ActivityCount)).ToListAsync();
    }
}

internal sealed class QueryCounter : DbCommandInterceptor
{
    public int Count { get; private set; }
    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result) { Count++; return result; }
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) { Count++; return ValueTask.FromResult(result); }
}

internal static class DbConnectionExtensions
{
    public static async Task<string> OpenAndVersionAsync(this DbConnection connection) { await using (connection) { await connection.OpenAsync(); return connection.ServerVersion; } }
}

internal sealed record Measurement(string Strategy, string Phase, int Iteration, double LatencyMilliseconds, int DatabaseQueryCount);
internal sealed record BenchmarkReport(string Task, DateTimeOffset RecordedAtUtc, int TicketCount, int Seed, int WarmupIterations, int MeasurementIterations,
    DateOnly From, DateOnly To, string DotnetVersion, string OperatingSystem, string PostgreSqlServerVersion, string RedisServerVersion,
    double PrecomputeSetupMilliseconds, IReadOnlyList<Measurement> Measurements);
