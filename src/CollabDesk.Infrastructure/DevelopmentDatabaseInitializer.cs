using CollabDesk.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Identity;

namespace CollabDesk.Infrastructure;

public static class DevelopmentDatabaseInitializer
{
    /// <summary>
    /// Limits automatic schema changes and fictional seed data to the Development environment.
    /// Every other environment must apply checked-in migrations explicitly.
    /// </summary>
    public static bool ShouldInitialize(string? environmentName) =>
        string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Applies checked-in migrations and seeds fictional users only in Development.
    /// Production migration execution is an explicit deployment operation.
    /// </summary>
    public static async Task MigrateAndSeedDevelopmentDatabaseAsync(this WebApplication app)
    {
        if (!ShouldInitialize(app.Environment.EnvironmentName))
        {
            return;
        }

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CollabDeskDbContext>();
        await db.Database.MigrateAsync();

        var passwordHasher = new PasswordHasher<AppUser>();
        var customer = new AppUser
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000101"),
            Email = "demo-customer@collabdesk.test",
            NormalizedEmail = "DEMO-CUSTOMER@COLLABDESK.TEST",
            PasswordHash = string.Empty,
            Role = Role.Customer
        };
        customer.PasswordHash = passwordHasher.HashPassword(customer, "local-fixture-password");
        var agent = new AppUser
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000102"),
            Email = "demo-agent@collabdesk.test",
            NormalizedEmail = "DEMO-AGENT@COLLABDESK.TEST",
            PasswordHash = string.Empty,
            Role = Role.Agent
        };
        agent.PasswordHash = passwordHasher.HashPassword(agent, "local-fixture-password");
        var administrator = new AppUser
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000103"),
            Email = "demo-admin@collabdesk.test",
            NormalizedEmail = "DEMO-ADMIN@COLLABDESK.TEST",
            PasswordHash = string.Empty,
            Role = Role.Administrator
        };
        administrator.PasswordHash = passwordHasher.HashPassword(administrator, "local-fixture-password");
        foreach (var fixture in new[] { customer, agent, administrator })
        {
            var existing = await db.Users.SingleOrDefaultAsync(user => user.Id == fixture.Id);
            if (existing is null)
            {
                db.Users.Add(fixture);
                continue;
            }

            existing.Email = fixture.Email;
            existing.NormalizedEmail = fixture.NormalizedEmail;
            existing.Role = fixture.Role;
            var hasFixturePassword = false;
            try
            {
                hasFixturePassword = passwordHasher.VerifyHashedPassword(existing, existing.PasswordHash, "local-fixture-password") != PasswordVerificationResult.Failed;
            }
            catch (FormatException)
            {
                // NET-001 development fixtures stored a non-hash placeholder; replace it below.
            }
            if (!hasFixturePassword)
            {
                existing.PasswordHash = fixture.PasswordHash;
            }
        }
        await db.SaveChangesAsync();
    }
}
