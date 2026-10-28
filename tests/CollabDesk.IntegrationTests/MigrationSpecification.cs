using CollabDesk.Domain;
using CollabDesk.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace CollabDesk.IntegrationTests;

public sealed class MigrationSpecification
{
    [Theory]
    [InlineData("Development", true)]
    [InlineData("development", true)]
    [InlineData("Staging", false)]
    [InlineData("Production", false)]
    [InlineData(null, false)]
    public void Development_initializer_runs_only_for_the_development_environment(
        string? environmentName,
        bool expected)
    {
        Assert.Equal(expected, DevelopmentDatabaseInitializer.ShouldInitialize(environmentName));
    }

    [Fact]
    public void Checked_in_migrations_are_discoverable_without_a_database_connection()
    {
        var options = new DbContextOptionsBuilder<CollabDeskDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
            .Options;
        using var db = new CollabDeskDbContext(options);

        Assert.Contains("202610240001_InitialCreate", db.Database.GetMigrations());
        Assert.Contains("20261024110000_AddChatReadReceipts", db.Database.GetMigrations());
        Assert.Contains("20261024105813_AddReportResultsAndAggregates", db.Database.GetMigrations());
    }

    [Fact]
    public void PostgreSql_model_marks_the_ticket_version_as_store_generated_concurrency()
    {
        var options = new DbContextOptionsBuilder<CollabDeskDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
            .Options;
        using var db = new CollabDeskDbContext(options);

        var version = db.Model.FindEntityType(typeof(Ticket))!
            .FindProperty(nameof(Ticket.Version))!;

        Assert.True(version.IsConcurrencyToken);
        Assert.Equal(ValueGenerated.OnAddOrUpdate, version.ValueGenerated);
    }
}
