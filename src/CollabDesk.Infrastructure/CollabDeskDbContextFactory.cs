using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CollabDesk.Infrastructure;

/// <summary>Provides the EF CLI a disposable local connection without embedding deployment settings.</summary>
public sealed class CollabDeskDbContextFactory : IDesignTimeDbContextFactory<CollabDeskDbContext>
{
    public CollabDeskDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? "Host=localhost;Database=collabdesk;Username=collabdesk;Password=collabdesk";
        var options = new DbContextOptionsBuilder<CollabDeskDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new CollabDeskDbContext(options);
    }
}
