using System.Security.Claims;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using CollabDesk.Application;
using CollabDesk.Api;
using CollabDesk.Domain;
using CollabDesk.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
var postgresConnection = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres must be configured.");
var redisConnection = builder.Configuration["Redis"]
    ?? throw new InvalidOperationException("Redis must be configured.");
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];

builder.Services.AddDbContext<CollabDeskDbContext>(o => o.UseNpgsql(postgresConnection));
builder.Services.AddScoped<TicketService>();
builder.Services.AddScoped<ReportService>();
var signalR = builder.Services.AddSignalR();
if (builder.Configuration.GetValue("SignalR:UseRedisBackplane", true))
    signalR.AddStackExchangeRedis(redisConnection);
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks().AddDbContextCheck<CollabDeskDbContext>();
builder.Services.AddStackExchangeRedisCache(o => o.Configuration = redisConnection);
builder.Services.AddSingleton<TicketReadCache>();
builder.Services.AddScoped<DurableWorkStore>();
builder.Services.AddScoped<IOutboxEventDispatcher, CacheInvalidatingOutboxDispatcher>();
builder.Services.AddHostedService<ReportWorker>();
builder.Services.AddHostedService<OutboxPublisher>();
builder.Services.AddOptions<AssistantStreamOptions>()
    .Bind(builder.Configuration.GetSection(AssistantStreamOptions.SectionName))
    .Validate(options => options.MaxTokens is > 0 and <= 256 &&
                         options.MaxOutputCharacters is > 0 and <= 65_536 &&
                         options.Timeout > TimeSpan.Zero && options.Timeout <= TimeSpan.FromMinutes(2) &&
                         options.InterTokenDelayMilliseconds is >= 0 and <= 5_000,
        "Assistant stream limits must be positive and within the supported maximums.")
    .ValidateOnStart();
builder.Services.AddSingleton<IAssistantStreamProvider, DeterministicAssistantStreamProvider>();
builder.Services.AddScoped<AssistantStreamService>();
builder.Services.AddSingleton<IPasswordHasher<AppUser>, PasswordHasher<AppUser>>();
builder.Services.AddScoped<IAuthorizationHandler, TicketParticipantHandler>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        // Browsers reject an insecure cookie carrying the __Host- prefix. The local HTTP
        // demonstration uses a distinct Development-only name; deployed environments retain
        // the host-only secure prefix below.
        options.Cookie.Name = builder.Environment.IsDevelopment() ? "collabdesk.auth.local" : "__Host-collabdesk.auth";
        options.Cookie.HttpOnly = true;
        // The Compose demo is intentionally HTTP-only and local. Production continues to require
        // a secure cookie; do not weaken this outside Development.
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.Path = "/";
        options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
        options.SlidingExpiration = false;
        options.Events.OnValidatePrincipal = async context =>
        {
            var sessionId = context.Principal?.FindFirstValue(ClaimNames.SessionId);
            var userId = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(sessionId, out var parsedSessionId) || !Guid.TryParse(userId, out var parsedUserId))
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync();
                return;
            }

            var db = context.HttpContext.RequestServices.GetRequiredService<CollabDeskDbContext>();
            var now = DateTimeOffset.UtcNow;
            var expiresAt = await db.AuthSessions.AsNoTracking().Where(session =>
                session.Id == parsedSessionId && session.UserId == parsedUserId &&
                session.RevokedAt == null).Select(session => session.ExpiresAt).FirstOrDefaultAsync();
            var valid = expiresAt > now;
            if (!valid)
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync();
            }
        };
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AgentOnly", policy => policy.RequireRole(Role.Agent.ToString()));
    options.AddPolicy("AdministratorOnly", policy => policy.RequireRole(Role.Administrator.ToString()));
    options.AddPolicy("TicketParticipant", policy => policy.RequireAuthenticatedUser().AddRequirements(new TicketParticipantRequirement()));
});
builder.Services.AddCors(options => options.AddPolicy("BrowserClients", policy =>
    policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = builder.Environment.IsDevelopment() ? "collabdesk.csrf.local" : "__Host-collabdesk.csrf";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.Path = "/";
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = builder.Configuration.GetValue("Security:LoginRateLimit:PermitLimit", 10),
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});

var app = builder.Build();
await app.MigrateAndSeedDevelopmentDatabaseAsync();
app.UseExceptionHandler(x => x.Run(async c =>
{
    c.Response.StatusCode = StatusCodes.Status500InternalServerError;
    c.Response.ContentType = "application/problem+json";
    await c.Response.WriteAsJsonAsync(new ProblemDetails
    {
        Status = StatusCodes.Status500InternalServerError,
        Title = "Unexpected server error",
        Detail = "Unexpected error",
        Type = "https://httpstatuses.com/500",
        Extensions = { ["traceId"] = c.TraceIdentifier }
    });
}));
app.UseStatusCodePages(async (StatusCodeContext statusContext) =>
{
    var response = statusContext.HttpContext.Response;
    if (response.HasStarted || response.ContentLength is > 0) return;
    response.ContentType = "application/problem+json";
    await response.WriteAsJsonAsync(new ProblemDetails
    {
        Status = response.StatusCode,
        Title = ReasonPhrases.GetReasonPhrase(response.StatusCode),
        Type = $"https://httpstatuses.com/{response.StatusCode}"
    });
});
app.UseCors("BrowserClients");
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.UseRateLimiter();
app.MapOpenApi();
app.MapHealthChecks("/health");

app.MapGet("/api/auth/csrf", (IAntiforgery antiforgery, HttpContext context) =>
{
    var tokens = antiforgery.GetAndStoreTokens(context);
    return Results.Ok(new { token = tokens.RequestToken });
});
app.MapPost("/api/auth/login", async (LoginRequest input, HttpContext context, CollabDeskDbContext db, IPasswordHasher<AppUser> passwordHasher, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(input.Email) || string.IsNullOrWhiteSpace(input.Password)) return Results.Unauthorized();
    var normalizedEmail = input.Email.Trim().ToUpperInvariant();
    var user = await db.Users.SingleOrDefaultAsync(candidate => candidate.NormalizedEmail == normalizedEmail, ct);
    if (user is null || passwordHasher.VerifyHashedPassword(user, user.PasswordHash, input.Password) == PasswordVerificationResult.Failed)
        return Results.Unauthorized();

    var expiresAt = DateTimeOffset.UtcNow.AddMinutes(30);
    var session = new AuthSession { UserId = user.Id, ExpiresAt = expiresAt };
    db.AuthSessions.Add(session);
    await db.SaveChangesAsync(ct);
    var principal = new ClaimsPrincipal(new ClaimsIdentity(
    [
        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new Claim(ClaimTypes.Name, user.Email),
        new Claim(ClaimTypes.Role, user.Role.ToString()),
        new Claim(ClaimNames.SessionId, session.Id.ToString())
    ], CookieAuthenticationDefaults.AuthenticationScheme));
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, new AuthenticationProperties
    {
        ExpiresUtc = expiresAt,
        IsPersistent = false,
        AllowRefresh = false
    });
    return Results.NoContent();
}).WithMetadata(new RequireAntiforgeryTokenAttribute(true)).RequireRateLimiting("login");

app.MapGet("/api/auth/me", [Authorize] (HttpContext context) =>
    Results.Ok(new CurrentUserDto(context.UserId(), context.User.Identity?.Name!, context.UserRole().ToString())));

app.MapPost("/api/auth/logout", [Authorize] async (HttpContext context, CollabDeskDbContext db, CancellationToken ct) =>
{
    if (context.TrySession(out var sessionId, out var userId))
    {
        var session = await db.AuthSessions.SingleOrDefaultAsync(item => item.Id == sessionId && item.UserId == userId, ct);
        if (session is not null && session.RevokedAt is null)
        {
            session.RevokedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.NoContent();
}).WithMetadata(new RequireAntiforgeryTokenAttribute(true));

app.MapPost("/api/tickets", [Authorize] async (HttpContext h, CreateTicketRequest input, TicketService service, CancellationToken ct) =>
{
    if (ApiContract.Validate(input) is { } validation) return validation;
    var key = h.Request.Headers["Idempotency-Key"].ToString();
    if (string.IsNullOrWhiteSpace(key) || key.Length > 200)
        return ApiContract.Problem(StatusCodes.Status400BadRequest, "Invalid idempotency key", "Idempotency-Key must be supplied and be at most 200 characters.");
    try
    {
        var (result, replay) = await service.CreateAsync(h.UserId(), key, new CreateTicket(input.Title!, input.Description!), ct);
        h.Response.Headers["Idempotency-Replayed"] = replay.ToString().ToLowerInvariant();
        return Results.Json(result, statusCode: replay ? StatusCodes.Status200OK : StatusCodes.Status201Created);
    }
    catch (IdempotencyConflictException e)
    {
        return ApiContract.Problem(StatusCodes.Status409Conflict, "Idempotency conflict", e.Message);
    }
    catch (IdempotencyInProgressException e)
    {
        return ApiContract.Problem(StatusCodes.Status409Conflict, "Idempotency request in progress", e.Message);
    }
}).WithMetadata(new RequireAntiforgeryTokenAttribute(true))
    .Produces<TicketDto>(StatusCodes.Status201Created)
    .Produces<TicketDto>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status409Conflict);

app.MapGet("/api/tickets", [Authorize] async (HttpContext h, TicketService service, CancellationToken ct, string? status = null, int page = 1, int pageSize = 25) =>
{
    if (page < 1 || pageSize is < 1 or > 100)
        return ApiContract.Problem(StatusCodes.Status400BadRequest, "Invalid pagination", "page must be at least 1 and pageSize must be between 1 and 100.");
    if (!ApiContract.TryParseStatus(status, out var parsedStatus))
        return ApiContract.Problem(StatusCodes.Status400BadRequest, "Invalid ticket status", "status must be Open, Claimed, or Resolved.");
    return Results.Ok(await service.ListAsync(h.UserId(), h.UserRole(), parsedStatus, page, pageSize, ct));
}).Produces<TicketPageDto>()
    .ProducesProblem(StatusCodes.Status400BadRequest);

app.MapGet("/api/agents/stats", [Authorize(Policy = "AgentOnly")] async (HttpContext h, TicketService service, CancellationToken ct) =>
    Results.Ok(await service.GetAgentStatsAsync(h.UserId(), ct)))
    .Produces<AgentStatsDto>();

app.MapPost("/api/reports", [Authorize(Policy = "AdministratorOnly")] async (CreateReportRequest input, HttpContext h, ReportService reports, CancellationToken ct) =>
{
    if (input.From == default || input.To == default || string.IsNullOrWhiteSpace(input.Type) || !new[] { "tickets", "agents" }.Contains(input.Type.Trim(), StringComparer.OrdinalIgnoreCase) ||
        input.From > input.To || input.To.DayNumber - input.From.DayNumber > 366)
        return ApiContract.Problem(StatusCodes.Status400BadRequest, "Invalid report request", "type must be tickets or agents and the inclusive UTC date range may not exceed 367 days.");
    var (job, reused) = await reports.CreateOrRefreshAsync(h.UserId(), new ReportDefinition(input.Type, input.From, input.To), input.Refresh, ct);
    h.Response.Headers["Report-Reused"] = reused.ToString().ToLowerInvariant();
    return Results.Json(ReportService.ToDto(job), statusCode: StatusCodes.Status202Accepted);
}).WithMetadata(new RequireAntiforgeryTokenAttribute(true))
    .Produces<ReportJobDto>(StatusCodes.Status202Accepted)
    .ProducesProblem(StatusCodes.Status400BadRequest);

app.MapGet("/api/reports/{id:long}", [Authorize(Policy = "AdministratorOnly")] async (long id, ReportService reports, CancellationToken ct) =>
{
    var job = await reports.GetAsync(id, ct);
    return job is null ? ApiContract.Problem(StatusCodes.Status404NotFound, "Report not found", "The report does not exist.") : Results.Ok(ReportService.ToDto(job));
}).Produces<ReportJobDto>().ProducesProblem(StatusCodes.Status404NotFound);

app.MapGet("/api/reports/{id:long}/result", [Authorize(Policy = "AdministratorOnly")] async (long id, ReportService reports, CancellationToken ct) =>
{
    var job = await reports.GetAsync(id, ct);
    if (job is null) return ApiContract.Problem(StatusCodes.Status404NotFound, "Report not found", "The report does not exist.");
    if (job.Status != ReportStatus.Completed || job.ResultJson is null) return ApiContract.Problem(StatusCodes.Status409Conflict, "Report is not ready", "Wait for the report to complete.");
    if (job.ResultExpiresAt is null || job.ResultExpiresAt <= DateTimeOffset.UtcNow) return ApiContract.Problem(StatusCodes.Status410Gone, "Report result expired", "Request this report again or use refresh.");
    return Results.Content(job.ResultJson, "application/json; charset=utf-8");
}).ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict).ProducesProblem(StatusCodes.Status410Gone);

app.MapGet("/api/reports/{id:long}/download/{format?}", [Authorize(Policy = "AdministratorOnly")] async (long id, string? format, ReportService reports, CancellationToken ct) =>
{
    var job = await reports.GetAsync(id, ct);
    if (job is null) return ApiContract.Problem(StatusCodes.Status404NotFound, "Report not found", "The report does not exist.");
    if (job.Status != ReportStatus.Completed || job.ResultJson is null) return ApiContract.Problem(StatusCodes.Status409Conflict, "Report is not ready", "Wait for the report to complete.");
    if (job.ResultExpiresAt is null || job.ResultExpiresAt <= DateTimeOffset.UtcNow) return ApiContract.Problem(StatusCodes.Status410Gone, "Report result expired", "Request this report again or use refresh.");
    format ??= "json";
    if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase)) return Results.File(System.Text.Encoding.UTF8.GetBytes(job.ResultJson), "application/json", $"report-{id}.json");
    if (!string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase)) return ApiContract.Problem(StatusCodes.Status400BadRequest, "Invalid download format", "format must be csv or json.");
    return Results.File(System.Text.Encoding.UTF8.GetBytes(ReportCsv.Serialize(job.ResultJson)), "text/csv; charset=utf-8", $"report-{id}.csv");
}).ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict).ProducesProblem(StatusCodes.Status410Gone);

app.MapDelete("/api/reports/{id:long}", [Authorize(Policy = "AdministratorOnly")] async (long id, ReportService reports, CancellationToken ct) =>
{
    return await reports.CancelAsync(id, ct) ? Results.NoContent() : ApiContract.Problem(StatusCodes.Status409Conflict, "Report cannot be cancelled", "Only queued reports can be cancelled.");
}).WithMetadata(new RequireAntiforgeryTokenAttribute(true)).ProducesProblem(StatusCodes.Status409Conflict);

app.MapPost("/api/tickets/{id:long}/claim", [Authorize(Policy = "AgentOnly")] async (long id, HttpContext h, TicketService service, CancellationToken ct) =>
{
    var (result, ticket) = await service.ClaimAsync(id, h.UserId(), ct);
    return result switch
    {
        ConditionalTicketResult.Updated => Results.Ok(ticket),
        ConditionalTicketResult.NotFound => ApiContract.Problem(StatusCodes.Status404NotFound, "Ticket not found", "The ticket does not exist."),
        _ => ApiContract.Problem(StatusCodes.Status409Conflict, "Ticket is not available", "Only an unassigned open ticket can be claimed.")
    };
}).WithMetadata(new RequireAntiforgeryTokenAttribute(true))
    .Produces<TicketDto>()
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status409Conflict);

app.MapPost("/api/tickets/{id:long}/status", [Authorize(Policy = "AgentOnly")] async (long id, StatusTransitionRequest input, HttpContext h, TicketService service, CancellationToken ct) =>
{
    if (ApiContract.Validate(input) is { } validation) return validation;
    if (!string.Equals(input.Status, TicketStatus.Resolved.ToString(), StringComparison.OrdinalIgnoreCase))
        return ApiContract.Problem(StatusCodes.Status400BadRequest, "Invalid status transition", "The only API transition is Claimed to Resolved.");
    var (result, ticket) = await service.ResolveAsync(id, h.UserId(), ct);
    return result switch
    {
        ConditionalTicketResult.Updated => Results.Ok(ticket),
        ConditionalTicketResult.NotFound => ApiContract.Problem(StatusCodes.Status404NotFound, "Ticket not found", "The ticket does not exist."),
        ConditionalTicketResult.Forbidden => ApiContract.Problem(StatusCodes.Status403Forbidden, "Ticket is owned by another agent", "Only the assigned agent may resolve this ticket."),
        _ => ApiContract.Problem(StatusCodes.Status409Conflict, "Invalid ticket transition", "Only a claimed ticket can be resolved.")
    };
}).WithMetadata(new RequireAntiforgeryTokenAttribute(true))
    .Produces<TicketDto>()
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status409Conflict);

app.MapGet("/api/tickets/{id:long}/messages", [Authorize] async (long id, HttpContext h, CollabDeskDbContext db, IAuthorizationService authorization, TicketService service, CancellationToken ct, int page = 1, int pageSize = 25) =>
{
    if (page < 1 || pageSize is < 1 or > 100)
        return ApiContract.Problem(StatusCodes.Status400BadRequest, "Invalid pagination", "page must be at least 1 and pageSize must be between 1 and 100.");
    var ticket = await db.Tickets.AsNoTracking().SingleOrDefaultAsync(ticket => ticket.Id == id, ct);
    if (ticket is null) return ApiContract.Problem(StatusCodes.Status404NotFound, "Ticket not found", "The ticket does not exist.");
    if (!(await authorization.AuthorizeAsync(h.User, ticket, "TicketParticipant")).Succeeded)
        return ApiContract.Problem(StatusCodes.Status403Forbidden, "Ticket access denied", "Only ticket participants can read message history.");
    return Results.Ok(await service.GetMessagesAsync(id, page, pageSize, ct));
}).Produces<IReadOnlyList<TicketMessageDto>>()
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status404NotFound);

app.MapPost("/api/assistant/stream", [Authorize] async (HttpContext h, AssistantRequest request, CollabDeskDbContext db, IAuthorizationService authorization, AssistantStreamService streamService, CancellationToken ct) =>
{
    if (request.TicketId <= 0)
        return ApiContract.Problem(StatusCodes.Status400BadRequest, "Invalid assistant request", "ticketId must be positive.");
    var ticket = await db.Tickets.FindAsync([request.TicketId], ct);
    if (ticket is null) return Results.NotFound();
    if (!(await authorization.AuthorizeAsync(h.User, ticket, "TicketParticipant")).Succeeded) return Results.Forbid();
    await streamService.StreamAsync(h, ticket.Id, ct);
    return Results.Empty;
}).WithMetadata(new RequireAntiforgeryTokenAttribute(true));

app.MapHub<ChatHub>("/ws/tickets").RequireAuthorization().RequireCors("BrowserClients");
app.MapFallbackToFile("index.html");
app.Run();

public record AssistantRequest(long TicketId);
public record LoginRequest(string Email, string Password);
public record CurrentUserDto(Guid Id, string Email, string Role);
public sealed record CreateTicketRequest(
    [property: Required, StringLength(160, MinimumLength = 1)] string? Title,
    [property: Required, StringLength(4000, MinimumLength = 1)] string? Description);
public sealed record StatusTransitionRequest(
    [property: Required, StringLength(32, MinimumLength = 1)] string? Status);
public sealed record CreateReportRequest(string Type, DateOnly From, DateOnly To, bool Refresh = false);
public static class ClaimNames { public const string SessionId = "collabdesk:session-id"; }
public static class HttpContextClaimsExtensions
{
    public static Guid UserId(this HttpContext context) => Guid.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    public static Role UserRole(this HttpContext context) =>
        Enum.TryParse<Role>(context.User.FindFirstValue(ClaimTypes.Role), out var role) ? role : throw new InvalidOperationException("Authenticated user has no valid role claim.");
    public static bool TrySession(this HttpContext context, out Guid sessionId, out Guid userId)
    {
        sessionId = default;
        userId = default;
        return Guid.TryParse(context.User.FindFirstValue(ClaimNames.SessionId), out sessionId) &&
            Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out userId);
    }
}

public static class ApiContract
{
    public static IResult? Validate(object value)
    {
        var results = new List<ValidationResult>();
        if (Validator.TryValidateObject(value, new ValidationContext(value), results, validateAllProperties: true)) return null;
        var errors = results.SelectMany(result => result.MemberNames.DefaultIfEmpty(string.Empty)
            .Select(member => new { member, error = result.ErrorMessage ?? "The value is invalid." }))
            .GroupBy(item => item.member, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(item => item.error).ToArray(), StringComparer.OrdinalIgnoreCase);
        return Results.ValidationProblem(errors, statusCode: StatusCodes.Status400BadRequest,
            title: "One or more validation errors occurred.", type: "https://httpstatuses.com/400");
    }

    public static IResult Problem(int statusCode, string title, string detail) => Results.Problem(
        statusCode: statusCode, title: title, detail: detail, type: $"https://httpstatuses.com/{statusCode}");

    public static bool TryParseStatus(string? value, out TicketStatus? status)
    {
        status = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!Enum.TryParse<TicketStatus>(value, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed)) return false;
        status = parsed;
        return true;
    }
}

public static class ReportCsv
{
    /// <summary>Flattens either report array into a safe RFC-4180-style CSV download.</summary>
    public static string Serialize(string json)
    {
        using var document = JsonDocument.Parse(json);
        var rows = new List<JsonElement>();
        foreach (var section in new[] { "daily", "monthly" })
            if (document.RootElement.TryGetProperty(section, out var array)) rows.AddRange(array.EnumerateArray());
        var columns = rows.SelectMany(row => row.EnumerateObject().Select(property => property.Name)).Distinct(StringComparer.Ordinal).ToArray();
        var csv = new System.Text.StringBuilder();
        csv.AppendLine(string.Join(',', columns.Select(Escape)));
        foreach (var row in rows)
            csv.AppendLine(string.Join(',', columns.Select(column => Escape(row.TryGetProperty(column, out var value) ? value.ToString() : string.Empty))));
        return csv.ToString();
    }
    private static string Escape(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
}

public sealed class TicketParticipantRequirement : IAuthorizationRequirement;
public sealed class TicketParticipantHandler : AuthorizationHandler<TicketParticipantRequirement, Ticket>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, TicketParticipantRequirement requirement, Ticket ticket)
    {
        if (Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) &&
            (ticket.CustomerId == userId || ticket.AssignedAgentId == userId)) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

[Authorize]
public sealed class ChatHub(CollabDeskDbContext db, IAuthorizationService authorization, TicketService tickets) : Hub
{
    private const int DefaultHistoryPageSize = 50;
    private const int MaxHistoryPageSize = 100;

    /// <summary>Authorizes and subscribes a connection. Invoke SyncHistory after reconnecting.</summary>
    public async Task JoinTicket(long ticketId)
    {
        await AuthorizeTicketAsync(ticketId, Context.ConnectionAborted);
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(ticketId), Context.ConnectionAborted);
    }

    /// <summary>Gets the next ordered history page after the client's last durable message ID.</summary>
    public async Task<ChatHistoryDto> SyncHistory(long ticketId, long afterMessageId = 0, int pageSize = DefaultHistoryPageSize)
    {
        await AuthorizeTicketAsync(ticketId, Context.ConnectionAborted);
        if (afterMessageId < 0) throw new HubException("afterMessageId must be zero or a persisted message ID");
        if (pageSize is < 1 or > MaxHistoryPageSize) throw new HubException($"pageSize must be between 1 and {MaxHistoryPageSize}");
        if (afterMessageId != 0 && !await db.Messages.AsNoTracking().AnyAsync(message =>
                message.TicketId == ticketId && message.Id == afterMessageId, Context.ConnectionAborted))
            throw new HubException("afterMessageId must identify a message in this ticket");
        return await tickets.GetMessagesAfterAsync(ticketId, afterMessageId, pageSize, Context.ConnectionAborted);
    }

    /// <summary>Persists a client-UUID-deduplicated message and broadcasts a new message once.</summary>
    public async Task<TicketMessageDto> SendMessage(long ticketId, Guid clientMessageId, string? body)
    {
        var (_, userId) = await AuthorizeTicketAsync(ticketId, Context.ConnectionAborted);
        if (clientMessageId == Guid.Empty) throw new HubException("clientMessageId must be a non-empty UUID");
        if (string.IsNullOrWhiteSpace(body) || body.Length > 4000) throw new HubException("Message body must be between 1 and 4000 characters");
        var message = await db.Messages.SingleOrDefaultAsync(item => item.TicketId == ticketId && item.ClientMessageId == clientMessageId, Context.ConnectionAborted)
            ?? new ChatMessage { TicketId = ticketId, ClientMessageId = clientMessageId, SenderId = userId, Body = body };
        if (message.SenderId != userId || !string.Equals(message.Body, body, StringComparison.Ordinal))
            throw new HubException("clientMessageId is already owned by a different message");
        var created = message.Id == 0;
        if (message.Id == 0)
        {
            db.Messages.Add(message);
            try
            {
                await db.SaveChangesAsync(Context.ConnectionAborted);
            }
            catch (DbUpdateException)
            {
                // The database unique index, not this process, decides a racing duplicate.
                db.ChangeTracker.Clear();
                message = await db.Messages.SingleAsync(item => item.TicketId == ticketId && item.ClientMessageId == clientMessageId, Context.ConnectionAborted);
                if (message.SenderId != userId || !string.Equals(message.Body, body, StringComparison.Ordinal))
                    throw new HubException("clientMessageId is already owned by a different message");
                created = false;
            }
        }
        var dto = new TicketMessageDto(message.Id, message.SenderId, message.ClientMessageId, message.Body, message.CreatedAt, message.TicketId);
        // A retry receives the original persisted message but does not generate a second event.
        if (created)
        {
            await tickets.InvalidateMessagesAsync(ticketId, Context.ConnectionAborted);
            await Clients.Group(GroupName(ticketId)).SendAsync("message", dto, Context.ConnectionAborted);
        }
        return dto;
    }

    /// <summary>Persists a participant's read cursor and notifies all of their active ticket sessions.</summary>
    public async Task MarkRead(long ticketId, long lastReadMessageId)
    {
        var (_, userId) = await AuthorizeTicketAsync(ticketId, Context.ConnectionAborted);
        if (lastReadMessageId <= 0 || !await db.Messages.AnyAsync(message => message.TicketId == ticketId && message.Id == lastReadMessageId, Context.ConnectionAborted))
            throw new HubException("lastReadMessageId must identify a message in this ticket");

        var receipt = await db.ChatReadReceipts.SingleOrDefaultAsync(item => item.TicketId == ticketId && item.ReaderId == userId, Context.ConnectionAborted);
        var readAt = DateTimeOffset.UtcNow;
        if (receipt is null)
        {
            receipt = new ChatReadReceipt { TicketId = ticketId, ReaderId = userId, LastReadMessageId = lastReadMessageId, ReadAt = readAt };
            db.ChatReadReceipts.Add(receipt);
        }
        else if (lastReadMessageId > receipt.LastReadMessageId)
        {
            receipt.LastReadMessageId = lastReadMessageId;
            receipt.ReadAt = readAt;
        }
        else
        {
            return; // Read cursors are monotonic; stale browser sessions cannot move them backwards.
        }
        try
        {
            await db.SaveChangesAsync(Context.ConnectionAborted);
        }
        catch (DbUpdateException)
        {
            // Concurrent browser sessions may create the same receipt. Reload it and keep the highest cursor.
            db.ChangeTracker.Clear();
            receipt = await db.ChatReadReceipts.SingleAsync(item => item.TicketId == ticketId && item.ReaderId == userId, Context.ConnectionAborted);
            if (lastReadMessageId <= receipt.LastReadMessageId) return;
            receipt.LastReadMessageId = lastReadMessageId;
            receipt.ReadAt = readAt;
            await db.SaveChangesAsync(Context.ConnectionAborted);
        }
        await Clients.Group(GroupName(ticketId)).SendAsync("readReceipt", new { ticketId, readerId = userId, lastReadMessageId, readAt }, Context.ConnectionAborted);
    }

    /// <summary>Ephemeral participant typing indicator. It is deliberately not persisted.</summary>
    public async Task SetTyping(long ticketId, bool isTyping)
    {
        var (_, userId) = await AuthorizeTicketAsync(ticketId, Context.ConnectionAborted);
        await Clients.OthersInGroup(GroupName(ticketId)).SendAsync("typing", new { ticketId, userId, isTyping }, Context.ConnectionAborted);
    }

    private async Task<(Ticket Ticket, Guid UserId)> AuthorizeTicketAsync(long ticketId, CancellationToken ct)
    {
        if (ticketId <= 0) throw new HubException("ticketId must be positive");
        if (!Guid.TryParse(Context.User?.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) ||
            !Guid.TryParse(Context.User?.FindFirstValue(ClaimNames.SessionId), out var sessionId))
        {
            Context.Abort();
            throw new HubException("An authenticated session is required");
        }
        // Compare the timestamp in memory so the same hub contract works with the SQLite test
        // provider, which cannot translate DateTimeOffset comparisons.
        var session = await db.AuthSessions.AsNoTracking().Where(item => item.Id == sessionId && item.UserId == userId)
            .Select(item => new { item.RevokedAt, item.ExpiresAt }).SingleOrDefaultAsync(ct);
        if (session is null || session.RevokedAt is not null || session.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            Context.Abort();
            throw new HubException("Session has expired or been revoked");
        }
        var ticket = await db.Tickets.FindAsync([ticketId], ct) ?? throw new HubException("Ticket not found");
        if (!(await authorization.AuthorizeAsync(Context.User!, ticket, "TicketParticipant")).Succeeded)
            throw new HubException("Ticket access denied");
        return (ticket, userId);
    }

    private static string GroupName(long ticketId) => $"ticket:{ticketId}";
}

public sealed class ReportWorker(IServiceScopeFactory scopes, ILogger<ReportWorker> log) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan LeaseRenewalInterval = TimeSpan.FromSeconds(20);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var work = scope.ServiceProvider.GetRequiredService<DurableWorkStore>();
                var reports = scope.ServiceProvider.GetRequiredService<ReportService>();
                await work.RecoverExpiredReportLeasesAsync(ct);
                var job = await work.ClaimReportAsync(Guid.NewGuid(), LeaseDuration, ct);
                if (job is null)
                {
                    await Task.Delay(PollInterval, ct);
                    continue;
                }

                using var generation = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var renewal = RenewLeaseAsync(job, generation);
                try
                {
                    var result = await reports.GenerateAsync(job, generation.Token);
                    if (!await work.CompleteReportAsync(job, result, ct))
                        log.LogWarning("Report {JobId} lost its lease before completion", job.Id);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && generation.IsCancellationRequested)
                {
                    // Another worker now owns (or will recover) this job. Do not mark it failed
                    // from this stale worker; the lease token fences that transition.
                    log.LogWarning("Report {JobId} generation stopped after its lease was lost", job.Id);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    await work.FailReportAsync(job, exception, ct);
                    log.LogError(exception, "Report {JobId} attempt {Attempt} failed", job.Id, job.Attempts);
                }
                finally
                {
                    generation.Cancel();
                    await renewal;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception exception)
            {
                log.LogError(exception, "Report worker coordination failed");
                await Task.Delay(PollInterval, ct);
            }
        }
    }

    private async Task RenewLeaseAsync(ClaimedReportJob job, CancellationTokenSource generation)
    {
        try
        {
            using var timer = new PeriodicTimer(LeaseRenewalInterval);
            while (await timer.WaitForNextTickAsync(generation.Token))
            {
                // Generation uses its own scoped DbContext. Renew in a separate scope so EF never
                // receives concurrent commands on the report-generation context.
                using var scope = scopes.CreateScope();
                var work = scope.ServiceProvider.GetRequiredService<DurableWorkStore>();
                if (await work.RenewReportLeaseAsync(job.Id, job.LeaseId, LeaseDuration, generation.Token)) continue;
                log.LogWarning("Report {JobId} lease renewal failed", job.Id);
                generation.Cancel();
                return;
            }
        }
        catch (OperationCanceledException) when (generation.IsCancellationRequested)
        {
            // Normal completion or shutdown. An incomplete report is recovered after its lease expires.
        }
        catch (Exception exception)
        {
            log.LogError(exception, "Report {JobId} lease renewal failed unexpectedly", job.Id);
            generation.Cancel();
        }
    }
}

/// <summary>Publishes persisted events with leases and bounded retry. Delivery is deliberately at-least-once.</summary>
public sealed class OutboxPublisher(IServiceScopeFactory scopes, ILogger<OutboxPublisher> log) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var work = scope.ServiceProvider.GetRequiredService<DurableWorkStore>();
                var dispatcher = scope.ServiceProvider.GetRequiredService<IOutboxEventDispatcher>();
                await work.RecoverExpiredOutboxLeasesAsync(ct);
                var item = await work.ClaimOutboxAsync(Guid.NewGuid(), LeaseDuration, ct);
                if (item is null)
                {
                    await Task.Delay(PollInterval, ct);
                    continue;
                }

                try
                {
                    // A process can crash after DispatchAsync and before this acknowledgement;
                    // dispatcher implementations therefore receive the stable event Id to deduplicate.
                    await dispatcher.DispatchAsync(new OutboxEvent
                    {
                        Id = item.Id,
                        Type = item.Type,
                        PayloadJson = item.PayloadJson,
                        Attempts = item.Attempts,
                        Status = OutboxStatus.Processing
                    }, ct);
                    if (!await work.MarkOutboxPublishedAsync(item, ct))
                        log.LogWarning("Outbox event {OutboxEventId} lost its lease before acknowledgement", item.Id);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    await work.FailOutboxAsync(item, exception, ct);
                    log.LogError(exception, "Outbox event {OutboxEventId} attempt {Attempt} failed", item.Id, item.Attempts);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception exception)
            {
                log.LogError(exception, "Outbox publisher coordination failed");
                await Task.Delay(PollInterval, ct);
            }
        }
    }
}

/// <summary>
/// The local cache side effect is safe to repeat. It is intentionally dispatched outside the
/// request transaction, so a Redis outage leaves the event pending instead of losing it.
/// </summary>
public sealed class CacheInvalidatingOutboxDispatcher(CollabDeskDbContext db, TicketReadCache cache) : IOutboxEventDispatcher
{
    public async Task DispatchAsync(OutboxEvent @event, CancellationToken ct)
    {
        switch (@event.Type)
        {
            case "ticket.created":
            {
                using var document = JsonDocument.Parse(@event.PayloadJson);
                var root = document.RootElement;
                Guid customerId;
                if (root.TryGetProperty("customerId", out var customerProperty) && customerProperty.TryGetGuid(out customerId))
                {
                    await cache.InvalidateTicketListsFromOutboxAsync(customerId, null, ct);
                    return;
                }

                // Backward compatibility for events committed before customerId was added.
                if (!root.TryGetProperty("ticketId", out var ticketProperty) || !ticketProperty.TryGetInt64(out var ticketId))
                    throw new InvalidOperationException($"Outbox event {@event.Id} has an invalid ticket.created payload.");
                customerId = await db.Tickets.Where(ticket => ticket.Id == ticketId)
                    .Select(ticket => ticket.CustomerId).SingleOrDefaultAsync(ct);
                if (customerId == Guid.Empty)
                    throw new InvalidOperationException($"Outbox event {@event.Id} references missing ticket {ticketId}.");
                await cache.InvalidateTicketListsFromOutboxAsync(customerId, null, ct);
                return;
            }
            case "report.completed":
                return; // The durable report row is the local, idempotent side effect.
            default:
                throw new InvalidOperationException($"Outbox event {@event.Id} has unsupported type '{@event.Type}'.");
        }
    }
}

public partial class Program;
