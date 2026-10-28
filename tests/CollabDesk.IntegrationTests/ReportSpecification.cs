using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CollabDesk.Domain;
using CollabDesk.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CollabDesk.IntegrationTests;

public sealed class ReportSpecification(AuthWebApplicationFactory factory) : IClassFixture<AuthWebApplicationFactory>
{
    [Fact]
    public async Task Reports_are_admin_only_and_identical_requests_reuse_one_durable_job()
    {
        using var customer = factory.CreateSecureClient();
        var customerCsrf = await LoginAsync(customer, AuthWebApplicationFactory.CustomerEmail);
        var denied = await PostAsync(customer, "/api/reports", Request(), customerCsrf);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        using var admin = factory.CreateSecureClient();
        var csrf = await LoginAsync(admin, AuthWebApplicationFactory.AdministratorEmail);
        var first = await PostAsync(admin, "/api/reports", Request(), csrf);
        var second = await PostAsync(admin, "/api/reports", Request(), csrf);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal("true", second.Headers.GetValues("Report-Reused").Single());
        var firstId = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        Assert.Equal(firstId, (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64());
        using var scope = factory.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<CollabDeskDbContext>().ReportJobs.CountAsync(job => job.Id == firstId));
    }

    [Fact]
    public async Task Completed_report_has_json_and_csv_downloads_and_expired_results_are_not_served()
    {
        using var admin = factory.CreateSecureClient();
        var csrf = await LoginAsync(admin, AuthWebApplicationFactory.AdministratorEmail);
        var create = await PostAsync(admin, "/api/reports", new { type = "agents", from = "2026-02-03", to = "2026-02-03" }, csrf);
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        await CompleteAsync(id, "{\"type\":\"agents\",\"daily\":[{\"day\":\"2026-02-03\",\"activityCount\":2}],\"monthly\":[]}");

        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/reports/{id}/result")).StatusCode);
        var json = await admin.GetAsync($"/api/reports/{id}/download/json");
        var csv = await admin.GetAsync($"/api/reports/{id}/download/csv");
        Assert.Equal("application/json", json.Content.Headers.ContentType!.MediaType);
        Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
        Assert.Contains("activityCount", await csv.Content.ReadAsStringAsync());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CollabDeskDbContext>();
            var job = await db.ReportJobs.SingleAsync(item => item.Id == id);
            job.ResultExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Gone, (await admin.GetAsync($"/api/reports/{id}/result")).StatusCode);
        var refreshed = await PostAsync(admin, "/api/reports", new { type = "agents", from = "2026-02-03", to = "2026-02-03", refresh = true }, csrf);
        var payload = await refreshed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(id, payload.GetProperty("id").GetInt64());
        Assert.Equal("Queued", payload.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Completed_report_without_a_cache_expiry_is_not_served_and_is_requeued()
    {
        using var admin = factory.CreateSecureClient();
        var csrf = await LoginAsync(admin, AuthWebApplicationFactory.AdministratorEmail);
        var create = await PostAsync(admin, "/api/reports", new { type = "tickets", from = "2026-02-04", to = "2026-02-04" }, csrf);
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        await CompleteAsync(id, "{\"type\":\"tickets\",\"daily\":[],\"monthly\":[]}");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CollabDeskDbContext>();
            var job = await db.ReportJobs.SingleAsync(item => item.Id == id);
            job.ResultExpiresAt = null;
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.Gone, (await admin.GetAsync($"/api/reports/{id}/result")).StatusCode);
        var repeat = await PostAsync(admin, "/api/reports", new { type = "tickets", from = "2026-02-04", to = "2026-02-04" }, csrf);
        var payload = await repeat.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(id, payload.GetProperty("id").GetInt64());
        Assert.Equal("Queued", payload.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Queued_report_can_be_cancelled()
    {
        using var admin = factory.CreateSecureClient();
        var csrf = await LoginAsync(admin, AuthWebApplicationFactory.AdministratorEmail);
        var create = await PostAsync(admin, "/api/reports", new { type = "tickets", from = "2026-03-03", to = "2026-03-03" }, csrf);
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/reports/{id}");
        delete.Headers.Add("X-CSRF-TOKEN", csrf);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.SendAsync(delete)).StatusCode);
        var status = await admin.GetFromJsonAsync<JsonElement>($"/api/reports/{id}");
        Assert.Equal("Cancelled", status.GetProperty("status").GetString());
    }

    private static object Request() => new { type = "tickets", from = "2026-01-02", to = "2026-01-03" };
    private async Task CompleteAsync(long id, string result)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CollabDeskDbContext>();
        var job = await db.ReportJobs.SingleAsync(item => item.Id == id);
        job.Status = ReportStatus.Completed;
        job.ResultJson = result;
        job.CompletedAt = DateTimeOffset.UtcNow;
        job.ResultExpiresAt = DateTimeOffset.UtcNow.AddHours(1);
        await db.SaveChangesAsync();
    }
    private static async Task<string> LoginAsync(HttpClient client, string email)
    {
        var login = await PostAsync(client, "/api/auth/login", new { email, password = AuthWebApplicationFactory.Password }, await CsrfAsync(client));
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        return await CsrfAsync(client);
    }
    private static async Task<string> CsrfAsync(HttpClient client) => (await (await client.GetAsync("/api/auth/csrf")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object body, string csrf)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        return await client.SendAsync(request);
    }
}

public sealed class ReportAggregationSpecification
{
    [Fact]
    public async Task Report_generation_precomputes_daily_monthly_response_time_and_agent_activity()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<CollabDeskDbContext>().UseSqlite(connection).Options;
        await using var db = new CollabDeskDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var agentId = Guid.NewGuid();
        var created = new DateTimeOffset(2026, 4, 7, 9, 0, 0, TimeSpan.Zero);
        db.Users.Add(new AppUser { Id = agentId, Email = "agent@fictional.test", NormalizedEmail = "AGENT@FICTIONAL.TEST", PasswordHash = "not-used", Role = Role.Agent });
        // This earlier ticket is outside the requested daily range but inside its calendar month.
        // It verifies that a newly built monthly materialization is not a partial-month total.
        db.Tickets.Add(new Ticket { CustomerId = Guid.NewGuid(), Title = "Earlier fictional", Description = "Monthly stats fixture", CreatedAt = created.AddDays(-1), Status = TicketStatus.Open });
        db.Tickets.Add(new Ticket { CustomerId = Guid.NewGuid(), Title = "Fictional", Description = "Stats fixture", CreatedAt = created, AssignedAgentId = agentId, AssignedAt = created.AddMinutes(30), ResolvedAt = created.AddHours(2), Status = TicketStatus.Resolved });
        db.Messages.Add(new ChatMessage { TicketId = 1, SenderId = agentId, ClientMessageId = Guid.NewGuid(), Body = "Fictional activity", CreatedAt = created.AddHours(1) });
        await db.SaveChangesAsync();
        var service = new CollabDesk.Application.ReportService(db);
        var (_, reused) = await service.CreateOrRefreshAsync(Guid.NewGuid(), new("agents", new DateOnly(2026, 4, 7), new DateOnly(2026, 4, 7)), false, CancellationToken.None);
        Assert.False(reused);
        var job = await db.ReportJobs.SingleAsync();
        var result = await service.GenerateAsync(new ClaimedReportJob(job.Id, job.RequestedById, job.Fingerprint, job.RequestJson, 1, Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(1)), CancellationToken.None);
        Assert.Contains("ResponseTimeTotalSeconds", result, StringComparison.Ordinal);
        var daily = await db.DailyAgentStats.SingleAsync();
        var monthly = await db.MonthlyAgentStats.SingleAsync();
        var monthlyTickets = await db.MonthlyTicketStats.SingleAsync();
        Assert.Equal(1, daily.ClaimedTickets);
        Assert.Equal(1, daily.ResolvedTickets);
        Assert.Equal(1800, daily.ResponseTimeTotalSeconds);
        Assert.Equal(1, daily.ActivityCount);
        Assert.Equal(daily.ResponseTimeTotalSeconds, monthly.ResponseTimeTotalSeconds);
        Assert.Equal(2, monthlyTickets.CreatedTickets);
        using var document = JsonDocument.Parse(result);
        Assert.Single(document.RootElement.GetProperty("daily").EnumerateArray());
    }
}
