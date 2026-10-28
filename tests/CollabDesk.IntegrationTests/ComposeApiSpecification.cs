using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CollabDesk.Api;
using CollabDesk.Domain;
using CollabDesk.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;
using Xunit;
using DomainRole = CollabDesk.Domain.Role;

namespace CollabDesk.IntegrationTests;

/// <summary>
/// Exercises the HTTP pipeline with the same disposable PostgreSQL and Redis instances used by CI.
/// The environment variables deliberately point only to a database whose name starts with
/// <c>collabdesk_test_</c>; ResetAsync refuses every other target before dropping it.
/// </summary>
[Collection("postgresql")]
public sealed class ComposeApiSpecification(ComposeApiFactory factory) : IClassFixture<ComposeApiFactory>, IAsyncLifetime
{
    public Task InitializeAsync() => factory.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [ComposeServicesFact]
    public async Task Full_api_uses_postgresql_and_redis_for_auth_roles_idempotency_reports_and_cache_invalidation()
    {
        using var customer = factory.CreateSecureClient();
        var customerCsrf = await LoginAsync(customer, ComposeApiFactory.CustomerEmail);
        var created = await PostAsync(customer, "/api/tickets", new { title = "Compose role fixture", description = "Fictional local ticket" }, customerCsrf,
            $"role-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var ticketId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();

        // A customer cookie cannot be escalated into an agent capability by choosing an agent-only route.
        var deniedClaim = await PostAsync(customer, $"/api/tickets/{ticketId}/claim", new { }, customerCsrf);
        Assert.Equal(HttpStatusCode.Forbidden, deniedClaim.StatusCode);

        using var agent = factory.CreateSecureClient();
        var agentCsrf = await LoginAsync(agent, ComposeApiFactory.AgentEmail);
        var acceptedClaim = await PostAsync(agent, $"/api/tickets/{ticketId}/claim", new { }, agentCsrf);
        Assert.Equal(HttpStatusCode.OK, acceptedClaim.StatusCode);

        // This read was cached by the real Redis service.  The claim advances its version, so the
        // next response must be reloaded and expose the new state instead of the stale Open entry.
        var list = await customer.GetFromJsonAsync<JsonElement>("/api/tickets?page=1&pageSize=100");
        Assert.Contains(list.GetProperty("items").EnumerateArray(), item =>
            item.GetProperty("id").GetInt64() == ticketId && item.GetProperty("status").GetString() == "Claimed");
        Assert.True(await factory.HasRedisKeysAsync("collabdesk:v1:ticket-lists:*") > 0);

        var idempotencyKey = $"twenty-way-{Guid.NewGuid():N}";
        var creates = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => PostAsync(customer, "/api/tickets",
            new { title = "Concurrent Compose ticket", description = "Fictional 20-way request" }, customerCsrf, idempotencyKey)));
        try
        {
            Assert.Equal(1, creates.Count(response => response.StatusCode == HttpStatusCode.Created));
            Assert.Equal(19, creates.Count(response => response.StatusCode == HttpStatusCode.OK));
            var ids = await Task.WhenAll(creates.Select(async response =>
                (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64()));
            Assert.Single(ids.Distinct());
        }
        finally
        {
            foreach (var response in creates) response.Dispose();
        }

        using var admin = factory.CreateSecureClient();
        var adminCsrf = await LoginAsync(admin, ComposeApiFactory.AdministratorEmail);
        var reports = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => PostAsync(admin, "/api/reports",
            new { type = "tickets", from = "2026-01-01", to = "2026-01-02" }, adminCsrf)));
        try
        {
            Assert.All(reports, response => Assert.Equal(HttpStatusCode.Accepted, response.StatusCode));
            var ids = await Task.WhenAll(reports.Select(async response =>
                (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64()));
            Assert.Single(ids.Distinct());
        }
        finally
        {
            foreach (var response in reports) response.Dispose();
        }

        await using var verification = factory.CreateDb();
        Assert.Equal(2, await verification.Tickets.CountAsync());
        Assert.Single(await verification.ReportJobs.ToListAsync());
        Assert.True((await verification.Database.GetAppliedMigrationsAsync()).Count() > 1);
    }

    [ComposeServicesFact]
    public async Task SignalR_long_polling_authenticates_participants_and_delivers_a_persisted_message()
    {
        using var customerHttp = factory.CreateSecureClient();
        using var agentHttp = factory.CreateSecureClient();
        var customerCookies = new CookieContainer();
        var agentCookies = new CookieContainer();
        await LoginAsync(customerHttp, ComposeApiFactory.CustomerEmail, customerCookies);
        await LoginAsync(agentHttp, ComposeApiFactory.AgentEmail, agentCookies);

        long ticketId;
        await using (var db = factory.CreateDb())
        {
            var customerId = await db.Users.Where(user => user.Email == ComposeApiFactory.CustomerEmail).Select(user => user.Id).SingleAsync();
            var agentId = await db.Users.Where(user => user.Email == ComposeApiFactory.AgentEmail).Select(user => user.Id).SingleAsync();
            var ticket = new Ticket
            {
                CustomerId = customerId,
                AssignedAgentId = agentId,
                Status = TicketStatus.Claimed,
                Title = "Compose SignalR ticket",
                Description = "Fictional long-polling transport fixture"
            };
            db.Tickets.Add(ticket);
            await db.SaveChangesAsync();
            ticketId = ticket.Id;
        }

        await using var customerHub = factory.CreateLongPollingHub(customerCookies);
        await using var agentHub = factory.CreateLongPollingHub(agentCookies);
        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        agentHub.On<JsonElement>("message", payload => received.TrySetResult(payload));
        await customerHub.StartAsync();
        await agentHub.StartAsync();
        await customerHub.InvokeAsync("JoinTicket", ticketId);
        await agentHub.InvokeAsync("JoinTicket", ticketId);

        var clientMessageId = Guid.NewGuid();
        await customerHub.InvokeAsync<JsonElement>("SendMessage", ticketId, clientMessageId, "Fictional live SignalR message");
        var delivered = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(delivered.GetProperty("id").GetInt64() > 0);
        Assert.Equal(clientMessageId, delivered.GetProperty("clientMessageId").GetGuid());

        await using var verification = factory.CreateDb();
        Assert.Single(await verification.Messages.Where(message => message.TicketId == ticketId && message.ClientMessageId == clientMessageId).ToListAsync());
    }

    private static async Task<string> LoginAsync(HttpClient client, string email, CookieContainer? connectionCookies = null)
    {
        var csrf = await CsrfAsync(client);
        var login = await PostAsync(client, "/api/auth/login", new { email, password = ComposeApiFactory.Password }, csrf);
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        if (connectionCookies is not null)
        {
            var authCookie = login.Headers.GetValues("Set-Cookie")
                .Single(value => value.StartsWith("__Host-collabdesk.auth=", StringComparison.Ordinal));
            connectionCookies.SetCookies(new Uri("https://localhost"), authCookie);
        }
        return await CsrfAsync(client);
    }

    private static async Task<string> CsrfAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf")).GetProperty("token").GetString()!;

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object body, string csrf, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        return client.SendAsync(request);
    }
}

public sealed class ComposeApiFactory : WebApplicationFactory<Program>
{
    public const string Password = "local-compose-test-password";
    public const string CustomerEmail = "compose-customer@collabdesk.test";
    public const string AgentEmail = "compose-agent@collabdesk.test";
    public const string AdministratorEmail = "compose-admin@collabdesk.test";

    private readonly string _postgres = Require("COLLABDESK_TEST_POSTGRES");
    private readonly string _redis = Require("COLLABDESK_TEST_REDIS");

    public ComposeApiFactory()
    {
        // The minimal-program bootstrap runs before ConfigureAppConfiguration.  These are
        // process-local test settings, never production credentials, and the web-host hook
        // below supplies the same endpoints to the application service registrations.
        Environment.SetEnvironmentVariable("ConnectionStrings__Postgres", _postgres);
        Environment.SetEnvironmentVariable("Redis", _redis);
        Environment.SetEnvironmentVariable("Cors__AllowedOrigins__0", "https://localhost");
        Environment.SetEnvironmentVariable("SignalR__UseRedisBackplane", "true");
        Environment.SetEnvironmentVariable("Security__LoginRateLimit__PermitLimit", "1000");
    }

    public CollabDeskDbContext CreateDb() => new(new DbContextOptionsBuilder<CollabDeskDbContext>().UseNpgsql(_postgres).Options);

    public async Task ResetAsync()
    {
        var database = new Npgsql.NpgsqlConnectionStringBuilder(_postgres).Database;
        if (string.IsNullOrWhiteSpace(database) || !database.StartsWith("collabdesk_test_", StringComparison.Ordinal))
            throw new InvalidOperationException("COLLABDESK_TEST_POSTGRES must target an isolated collabdesk_test_* database.");

        await using (var db = CreateDb())
        {
            await db.Database.EnsureDeletedAsync();
            await db.Database.MigrateAsync();
            var passwordHasher = new PasswordHasher<AppUser>();
            db.Users.AddRange(CreateUser(CustomerEmail, DomainRole.Customer, passwordHasher), CreateUser(AgentEmail, DomainRole.Agent, passwordHasher),
                CreateUser(AdministratorEmail, DomainRole.Administrator, passwordHasher));
            await db.SaveChangesAsync();
        }

        var redisOptions = ConfigurationOptions.Parse(_redis);
        redisOptions.AllowAdmin = true; // FLUSHDB is confined to the disposable Compose Redis service.
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(redisOptions);
        foreach (var endpoint in multiplexer.GetEndPoints())
            await multiplexer.GetServer(endpoint).FlushDatabaseAsync();
    }

    public async Task<long> HasRedisKeysAsync(string pattern)
    {
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(_redis);
        long count = 0;
        foreach (var endpoint in multiplexer.GetEndPoints())
            count += multiplexer.GetServer(endpoint).Keys(pattern: pattern).LongCount();
        return count;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = _postgres,
            ["Redis"] = _redis,
            ["Cors:AllowedOrigins:0"] = "https://localhost",
            ["SignalR:UseRedisBackplane"] = "true",
            ["Security:LoginRateLimit:PermitLimit"] = "1000"
        }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHostedService>(); // Worker lifecycle is verified through durable multi-connection tests.
            services.RemoveAll<IDistributedCache>();
            services.AddStackExchangeRedisCache(options => options.Configuration = _redis);
            foreach (var descriptor in services.Where(descriptor => descriptor.ServiceType == typeof(CollabDeskDbContext) ||
                         descriptor.ServiceType == typeof(DbContextOptions<CollabDeskDbContext>) || descriptor.ServiceType == typeof(DbContextOptions)).ToList())
                services.Remove(descriptor);
            services.RemoveAll<IDbContextOptionsConfiguration<CollabDeskDbContext>>();
            services.AddDbContext<CollabDeskDbContext>(options => options.UseNpgsql(_postgres));
        });
    }

    public HttpClient CreateSecureClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"), HandleCookies = true, AllowAutoRedirect = false
    });

    public HubConnection CreateLongPollingHub(CookieContainer cookies) => new HubConnectionBuilder()
        .WithUrl("https://localhost/ws/tickets", options =>
        {
            options.Transports = HttpTransportType.LongPolling;
            options.Cookies = cookies;
            // TestServer's custom handler is not an HttpClientHandler, so explicitly forward
            // the authenticated secure cookie as well as supplying the container.
            options.Headers["Cookie"] = cookies.GetCookieHeader(new Uri("https://localhost"));
            options.HttpMessageHandlerFactory = _ => Server.CreateHandler();
        })
        .Build();

    private static string Require(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"{name} is required. Start docker compose with docker-compose.test.yml first.");

    private static AppUser CreateUser(string email, DomainRole role, IPasswordHasher<AppUser> passwordHasher)
    {
        var user = new AppUser { Email = email, NormalizedEmail = email.ToUpperInvariant(), PasswordHash = string.Empty, Role = role };
        user.PasswordHash = passwordHasher.HashPassword(user, Password);
        return user;
    }
}

/// <summary>CI supplies both local Compose endpoints; a source-only run remains dependency-free.</summary>
public sealed class ComposeServicesFactAttribute : FactAttribute
{
    public ComposeServicesFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COLLABDESK_TEST_POSTGRES")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COLLABDESK_TEST_REDIS")))
            Skip = "Set COLLABDESK_TEST_POSTGRES and COLLABDESK_TEST_REDIS after starting docker-compose.test.yml.";
    }
}
