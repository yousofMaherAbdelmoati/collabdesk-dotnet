using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CollabDesk.Domain;
using CollabDesk.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace CollabDesk.IntegrationTests;

public sealed class AuthenticationSpecification(AuthWebApplicationFactory factory) : IClassFixture<AuthWebApplicationFactory>
{
    [Fact]
    public async Task Login_uses_hashed_passwords_and_logout_revokes_the_server_session()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CollabDeskDbContext>();
            var customer = await db.Users.SingleAsync(user => user.Email == AuthWebApplicationFactory.CustomerEmail);
            Assert.NotEqual(AuthWebApplicationFactory.Password, customer.PasswordHash);
            Assert.Equal(
                PasswordVerificationResult.Success,
                new PasswordHasher<AppUser>().VerifyHashedPassword(customer, customer.PasswordHash, AuthWebApplicationFactory.Password));
        }

        using var client = factory.CreateSecureClient();
        var invalidLogin = await PostJsonWithCsrfAsync(client, "/api/auth/login", new { email = AuthWebApplicationFactory.CustomerEmail, password = "incorrect" });
        Assert.Equal(HttpStatusCode.Unauthorized, invalidLogin.StatusCode);

        var csrfToken = await LoginAsync(client, AuthWebApplicationFactory.CustomerEmail);
        var create = await PostJsonWithCsrfAsync(client, "/api/tickets", new { title = "Logout verification", description = "Fictional ticket" }, csrfToken, "logout-ticket");
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var logout = await PostJsonWithCsrfAsync(client, "/api/auth/logout", new { }, csrfToken);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CollabDeskDbContext>();
            Assert.Contains(await db.AuthSessions.ToListAsync(), session => session.RevokedAt is not null);
        }

        var afterLogout = await PostJsonWithCsrfAsync(client, "/api/tickets", new { title = "Rejected", description = "Session is revoked" }, await GetCsrfAsync(client), "after-logout");
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
    }

    [Fact]
    public async Task Authenticated_identity_endpoint_returns_the_server_issued_role()
    {
        using var client = factory.CreateSecureClient();
        await LoginAsync(client, AuthWebApplicationFactory.AgentEmail);

        var identity = await client.GetFromJsonAsync<JsonElement>("/api/auth/me");

        Assert.Equal(AuthWebApplicationFactory.AgentEmail, identity.GetProperty("email").GetString());
        Assert.Equal(Role.Agent.ToString(), identity.GetProperty("role").GetString());
        Assert.NotEqual(Guid.Empty, identity.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Restricted_endpoints_reject_anonymous_and_customer_role_misuse()
    {
        using var anonymous = factory.CreateSecureClient();
        var anonymousCreate = await PostJsonWithCsrfAsync(anonymous, "/api/tickets", new { title = "No identity", description = "Must be rejected" }, await GetCsrfAsync(anonymous), "anonymous");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousCreate.StatusCode);

        using var customer = factory.CreateSecureClient();
        var customerCsrf = await LoginAsync(customer, AuthWebApplicationFactory.CustomerEmail);
        var created = await PostJsonWithCsrfAsync(customer, "/api/tickets", new { title = "Agent-only claim", description = "Fictional ticket" }, customerCsrf, "customer-ticket");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var ticketId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();

        var customerClaim = await PostJsonWithCsrfAsync(customer, $"/api/tickets/{ticketId}/claim", new { }, customerCsrf);
        Assert.Equal(HttpStatusCode.Forbidden, customerClaim.StatusCode);

        using var agent = factory.CreateSecureClient();
        var agentCsrf = await LoginAsync(agent, AuthWebApplicationFactory.AgentEmail);
        var agentClaim = await PostJsonWithCsrfAsync(agent, $"/api/tickets/{ticketId}/claim", new { }, agentCsrf);
        Assert.Equal(HttpStatusCode.OK, agentClaim.StatusCode);
    }

    [Fact]
    public async Task An_expired_server_session_cannot_use_a_still_present_cookie()
    {
        using var client = factory.CreateSecureClient();
        var csrfToken = await LoginAsync(client, AuthWebApplicationFactory.CustomerEmail);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CollabDeskDbContext>();
            var userId = await db.Users.Where(user => user.Email == AuthWebApplicationFactory.CustomerEmail).Select(user => user.Id).SingleAsync();
            var sessions = await db.AuthSessions.Where(item => item.UserId == userId).ToListAsync();
            foreach (var session in sessions) session.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        }

        var expiredRequest = await PostJsonWithCsrfAsync(client, "/api/tickets", new { title = "Expired", description = "Must be rejected" }, csrfToken, "expired-session");
        Assert.Equal(HttpStatusCode.Unauthorized, expiredRequest.StatusCode);
    }

    [Fact]
    public async Task A_user_cannot_access_another_customers_ticket()
    {
        using var owner = factory.CreateSecureClient();
        var ownerCsrf = await LoginAsync(owner, AuthWebApplicationFactory.CustomerEmail);
        var created = await PostJsonWithCsrfAsync(owner, "/api/tickets", new { title = "Private ticket", description = "Fictional private issue" }, ownerCsrf, "private-ticket");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var ticketId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();

        using var otherCustomer = factory.CreateSecureClient();
        var otherCsrf = await LoginAsync(otherCustomer, AuthWebApplicationFactory.OtherCustomerEmail);
        var privateAssistantRequest = await PostJsonWithCsrfAsync(otherCustomer, "/api/assistant/stream", new { ticketId }, otherCsrf);
        Assert.Equal(HttpStatusCode.Forbidden, privateAssistantRequest.StatusCode);
    }

    [Fact]
    public async Task SignalR_negotiate_requires_an_authenticated_session()
    {
        using var client = factory.CreateSecureClient();
        var negotiate = await client.PostAsync("/ws/tickets/negotiate?negotiateVersion=1", new StringContent(string.Empty));
        Assert.Equal(HttpStatusCode.Unauthorized, negotiate.StatusCode);
    }

    [Fact]
    public async Task SignalR_cors_policy_allows_only_the_configured_origin()
    {
        using var client = factory.CreateSecureClient();
        var allowed = new HttpRequestMessage(HttpMethod.Options, "/ws/tickets/negotiate");
        allowed.Headers.Add("Origin", "https://localhost");
        allowed.Headers.Add("Access-Control-Request-Method", "POST");
        var allowedResponse = await client.SendAsync(allowed);
        Assert.Equal("https://localhost", allowedResponse.Headers.GetValues("Access-Control-Allow-Origin").Single());

        var rejected = new HttpRequestMessage(HttpMethod.Options, "/ws/tickets/negotiate");
        rejected.Headers.Add("Origin", "https://untrusted.example");
        rejected.Headers.Add("Access-Control-Request-Method", "POST");
        var rejectedResponse = await client.SendAsync(rejected);
        Assert.False(rejectedResponse.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Ticket_contract_validates_input_and_uses_problem_details()
    {
        using var client = factory.CreateSecureClient();
        var csrf = await LoginAsync(client, AuthWebApplicationFactory.CustomerEmail);
        var response = await PostJsonWithCsrfAsync(client, "/api/tickets", new { title = "", description = "Fictional invalid request" }, csrf, "invalid-ticket");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(400, problem.GetProperty("status").GetInt32());
        Assert.True(problem.TryGetProperty("errors", out _));
    }

    [Fact]
    public async Task Ticket_contract_is_described_in_the_typed_openapi_document()
    {
        using var client = factory.CreateSecureClient();
        var documentResponse = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, documentResponse.StatusCode);
        var document = await documentResponse.Content.ReadFromJsonAsync<JsonElement>();
        var ticketPath = document.GetProperty("paths").GetProperty("/api/tickets");
        Assert.True(ticketPath.TryGetProperty("get", out var get));
        Assert.True(ticketPath.TryGetProperty("post", out var post));
        Assert.True(get.GetProperty("responses").TryGetProperty("200", out _));
        Assert.True(post.GetProperty("responses").TryGetProperty("201", out _));
        Assert.True(post.GetProperty("responses").TryGetProperty("409", out _));
    }

    [Fact]
    public async Task Ticket_list_is_paginated_and_customer_scoped()
    {
        using var owner = factory.CreateSecureClient();
        var ownerCsrf = await LoginAsync(owner, AuthWebApplicationFactory.CustomerEmail);
        var created = await PostJsonWithCsrfAsync(owner, "/api/tickets", new { title = "Owned list ticket", description = "Fictional list fixture" }, ownerCsrf, $"owner-list-{Guid.NewGuid():N}");
        var ownedId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();

        using var other = factory.CreateSecureClient();
        var otherCsrf = await LoginAsync(other, AuthWebApplicationFactory.OtherCustomerEmail);
        var otherCreated = await PostJsonWithCsrfAsync(other, "/api/tickets", new { title = "Other list ticket", description = "Fictional list fixture" }, otherCsrf, $"other-list-{Guid.NewGuid():N}");
        var otherId = (await otherCreated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();

        var list = await owner.GetFromJsonAsync<JsonElement>("/api/tickets?page=1&pageSize=1&status=Open");
        Assert.Equal(1, list.GetProperty("page").GetInt32());
        Assert.Equal(1, list.GetProperty("pageSize").GetInt32());
        Assert.True(list.GetProperty("totalCount").GetInt32() >= 1);
        var ids = list.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetInt64()).ToArray();
        Assert.DoesNotContain(otherId, ids);
        var fullList = await owner.GetFromJsonAsync<JsonElement>("/api/tickets?page=1&pageSize=100");
        Assert.Contains(ownedId, fullList.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetInt64()));
    }

    [Fact]
    public async Task Message_history_requires_ticket_participation_and_status_transition_requires_owner()
    {
        using var customer = factory.CreateSecureClient();
        var customerCsrf = await LoginAsync(customer, AuthWebApplicationFactory.CustomerEmail);
        var created = await PostJsonWithCsrfAsync(customer, "/api/tickets", new { title = "History ticket", description = "Fictional history fixture" }, customerCsrf, $"history-{Guid.NewGuid():N}");
        var ticketId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        Guid customerId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CollabDeskDbContext>();
            customerId = await db.Users.Where(user => user.Email == AuthWebApplicationFactory.CustomerEmail).Select(user => user.Id).SingleAsync();
            db.Messages.Add(new ChatMessage { TicketId = ticketId, SenderId = customerId, ClientMessageId = Guid.NewGuid(), Body = "Fictional persisted message" });
            await db.SaveChangesAsync();
        }

        var history = await customer.GetFromJsonAsync<JsonElement>($"/api/tickets/{ticketId}/messages?page=1&pageSize=10");
        Assert.Single(history.EnumerateArray());

        using var other = factory.CreateSecureClient();
        await LoginAsync(other, AuthWebApplicationFactory.OtherCustomerEmail);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"/api/tickets/{ticketId}/messages")).StatusCode);

        using var agent = factory.CreateSecureClient();
        var agentCsrf = await LoginAsync(agent, AuthWebApplicationFactory.AgentEmail);
        Assert.Equal(HttpStatusCode.OK, (await PostJsonWithCsrfAsync(agent, $"/api/tickets/{ticketId}/claim", new { }, agentCsrf)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostJsonWithCsrfAsync(agent, $"/api/tickets/{ticketId}/status", new { status = "Resolved" }, agentCsrf)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PostJsonWithCsrfAsync(agent, $"/api/tickets/{ticketId}/status", new { status = "Resolved" }, agentCsrf)).StatusCode);
    }

    private static async Task<string> LoginAsync(HttpClient client, string email)
    {
        var login = await PostJsonWithCsrfAsync(client, "/api/auth/login", new { email, password = AuthWebApplicationFactory.Password });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        return await GetCsrfAsync(client);
    }

    private static async Task<string> GetCsrfAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/auth/csrf");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    }

    private static Task<HttpResponseMessage> PostJsonWithCsrfAsync(HttpClient client, string path, object body, string? csrfToken = null, string? idempotencyKey = null)
    {
        return SendAsync();

        async Task<HttpResponseMessage> SendAsync()
        {
            csrfToken ??= await GetCsrfAsync(client);
            var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
            request.Headers.Add("X-CSRF-TOKEN", csrfToken);
            if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
            return await client.SendAsync(request);
        }
    }
}

public sealed class AuthWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string Password = "local-test-password";
    public const string CustomerEmail = "customer-a@collabdesk.test";
    public const string OtherCustomerEmail = "customer-b@collabdesk.test";
    public const string AgentEmail = "agent@collabdesk.test";
    public const string AdministratorEmail = "admin@collabdesk.test";
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly IServiceProvider _sqliteServices = new ServiceCollection().AddEntityFrameworkSqlite().BuildServiceProvider();

    public AuthWebApplicationFactory()
    {
        // WebApplicationFactory invokes the minimal-program bootstrap before its in-memory
        // configuration hook. These values satisfy that bootstrap; ConfigureWebHost replaces
        // both data services with the isolated SQLite test doubles below.
        Environment.SetEnvironmentVariable("ConnectionStrings__Postgres", "Host=unused;Database=unused;Username=unused;Password=unused");
        Environment.SetEnvironmentVariable("Redis", "localhost:0");
        Environment.SetEnvironmentVariable("Cors__AllowedOrigins__0", "https://localhost");
        Environment.SetEnvironmentVariable("SignalR__UseRedisBackplane", "false");
        Environment.SetEnvironmentVariable("Security__LoginRateLimit__PermitLimit", "1000");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = "Host=unused;Database=unused;Username=unused;Password=unused",
            ["Redis"] = "localhost:0",
            ["Cors:AllowedOrigins:0"] = "https://localhost",
            ["SignalR:UseRedisBackplane"] = "false",
            ["Security:LoginRateLimit:PermitLimit"] = "1000"
        }));
        builder.ConfigureServices(services =>
        {
            foreach (var descriptor in services.Where(descriptor =>
                         descriptor.ServiceType == typeof(CollabDeskDbContext) ||
                         descriptor.ServiceType == typeof(DbContextOptions<CollabDeskDbContext>) ||
                         descriptor.ServiceType == typeof(DbContextOptions)).ToList())
            {
                services.Remove(descriptor);
            }
            services.RemoveAll<IHostedService>();
            // The application uses Redis in production. Tests use a local distributed-cache
            // implementation so route tests do not depend on a network listener.
            services.RemoveAll<IDistributedCache>();
            services.AddDistributedMemoryCache();
            services.RemoveAll<IDbContextOptionsConfiguration<CollabDeskDbContext>>();
            services.AddSingleton(_ => _connection);
            services.AddDbContext<CollabDeskDbContext>((serviceProvider, options) =>
                options.UseSqlite(serviceProvider.GetRequiredService<SqliteConnection>()).UseInternalServiceProvider(_sqliteServices));
        });
    }

    public HttpClient CreateSecureClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        HandleCookies = true,
        AllowAutoRedirect = false
    });

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CollabDeskDbContext>();
        await db.Database.EnsureCreatedAsync();
        var passwordHasher = new PasswordHasher<AppUser>();
        db.Users.AddRange(CreateUser(CustomerEmail, Role.Customer, passwordHasher), CreateUser(OtherCustomerEmail, Role.Customer, passwordHasher), CreateUser(AgentEmail, Role.Agent, passwordHasher), CreateUser(AdministratorEmail, Role.Administrator, passwordHasher));
        await db.SaveChangesAsync();
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await _connection.DisposeAsync();
        if (_sqliteServices is IDisposable disposable) disposable.Dispose();
    }

    private static AppUser CreateUser(string email, Role role, IPasswordHasher<AppUser> passwordHasher)
    {
        var user = new AppUser { Email = email, NormalizedEmail = email.ToUpperInvariant(), PasswordHash = string.Empty, Role = role };
        user.PasswordHash = passwordHasher.HashPassword(user, Password);
        return user;
    }
}
