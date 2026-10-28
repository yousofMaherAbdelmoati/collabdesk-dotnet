using System.Security.Claims;
using CollabDesk.Application;
using CollabDesk.Domain;
using CollabDesk.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CollabDesk.IntegrationTests;

public sealed class ChatHubSpecification(AuthWebApplicationFactory factory) : IClassFixture<AuthWebApplicationFactory>
{
    [Fact]
    public async Task Two_authorized_sessions_receive_one_new_message_and_duplicate_retries_do_not_rebroadcast()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var fixture = await ChatFixture.CreateAsync(scope.ServiceProvider, assignedToAgent: true);
        var clients = new RecordingClients();
        var groups = new RecordingGroups();
        var customer = fixture.CreateHub(fixture.Customer, clients, groups, "customer-browser-a");
        var agent = fixture.CreateHub(fixture.Agent, clients, groups, "agent-browser-b");

        await customer.JoinTicket(fixture.Ticket.Id);
        await agent.JoinTicket(fixture.Ticket.Id);
        Assert.Equal(2, groups.Additions.Count(addition => addition.Group == $"ticket:{fixture.Ticket.Id}"));

        var clientMessageId = Guid.NewGuid();
        var first = await customer.SendMessage(fixture.Ticket.Id, clientMessageId, "Fictional browser message");
        var retry = await customer.SendMessage(fixture.Ticket.Id, clientMessageId, "Fictional browser message");

        Assert.Equal(first.Id, retry.Id);
        Assert.Single(await fixture.Db.Messages.Where(message => message.TicketId == fixture.Ticket.Id).ToListAsync());
        Assert.Single(clients.Calls, call => call.Target == "message");

        await customer.SetTyping(fixture.Ticket.Id, true);
        Assert.Single(clients.Calls, call => call.Target == "typing");
        await Assert.ThrowsAsync<HubException>(() => customer.SendMessage(fixture.Ticket.Id, Guid.Empty, "invalid UUID"));
        await Assert.ThrowsAsync<HubException>(() => customer.SyncHistory(fixture.Ticket.Id, 0, 101));
    }

    [Fact]
    public async Task Unauthorized_subscription_is_rejected()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var fixture = await ChatFixture.CreateAsync(scope.ServiceProvider, assignedToAgent: false);
        var hub = fixture.CreateHub(fixture.OtherCustomer, new RecordingClients(), new RecordingGroups(), "unrelated-browser");

        var error = await Assert.ThrowsAsync<HubException>(() => hub.JoinTicket(fixture.Ticket.Id));

        Assert.Equal("Ticket access denied", error.Message);
    }

    [Fact]
    public async Task Reconnected_session_recovers_ordered_paginated_history_and_persists_monotonic_read_receipts()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var fixture = await ChatFixture.CreateAsync(scope.ServiceProvider, assignedToAgent: true);
        var messages = new[]
        {
            new ChatMessage { TicketId = fixture.Ticket.Id, SenderId = fixture.Customer.UserId, ClientMessageId = Guid.NewGuid(), Body = "one" },
            new ChatMessage { TicketId = fixture.Ticket.Id, SenderId = fixture.Agent.UserId, ClientMessageId = Guid.NewGuid(), Body = "two" },
            new ChatMessage { TicketId = fixture.Ticket.Id, SenderId = fixture.Customer.UserId, ClientMessageId = Guid.NewGuid(), Body = "three" }
        };
        fixture.Db.Messages.AddRange(messages);
        await fixture.Db.SaveChangesAsync();

        var clients = new RecordingClients();
        var reconnectedCustomer = fixture.CreateHub(fixture.Customer, clients, new RecordingGroups(), "customer-browser-reconnected");
        await reconnectedCustomer.JoinTicket(fixture.Ticket.Id);
        var firstPage = await reconnectedCustomer.SyncHistory(fixture.Ticket.Id, 0, 2);
        var secondPage = await reconnectedCustomer.SyncHistory(fixture.Ticket.Id, firstPage.NextAfterMessageId!.Value, 2);

        Assert.Equal(new[] { messages[0].Id, messages[1].Id }, firstPage.Messages.Select(message => message.Id));
        Assert.Equal(new[] { messages[2].Id }, secondPage.Messages.Select(message => message.Id));
        Assert.Null(secondPage.NextAfterMessageId);

        await reconnectedCustomer.MarkRead(fixture.Ticket.Id, messages[1].Id);
        await reconnectedCustomer.MarkRead(fixture.Ticket.Id, messages[0].Id); // An older browser state cannot regress the receipt.
        var receipt = await fixture.Db.ChatReadReceipts.SingleAsync(item => item.TicketId == fixture.Ticket.Id && item.ReaderId == fixture.Customer.UserId);
        Assert.Equal(messages[1].Id, receipt.LastReadMessageId);
        Assert.Single(clients.Calls, call => call.Target == "readReceipt");
    }

    [Fact]
    public async Task Revoked_session_cannot_send_or_emit_typing_after_connection()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var fixture = await ChatFixture.CreateAsync(scope.ServiceProvider, assignedToAgent: false);
        var context = new TestHubCallerContext(fixture.Customer.Principal, "customer-browser");
        var hub = fixture.CreateHub(fixture.Customer, new RecordingClients(), new RecordingGroups(), context);
        await hub.JoinTicket(fixture.Ticket.Id);

        var session = await fixture.Db.AuthSessions.SingleAsync(item => item.Id == fixture.Customer.SessionId);
        session.RevokedAt = DateTimeOffset.UtcNow;
        await fixture.Db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<HubException>(() => hub.SendMessage(fixture.Ticket.Id, Guid.NewGuid(), "must not persist"));
        Assert.Equal("Session has expired or been revoked", error.Message);
        Assert.True(context.Aborted);
        Assert.Empty(await fixture.Db.Messages.Where(message => message.TicketId == fixture.Ticket.Id).ToListAsync());
    }

    private sealed class ChatFixture(CollabDeskDbContext db, IAuthorizationService authorization, Ticket ticket, SessionUser customer, SessionUser otherCustomer, SessionUser agent)
    {
        public CollabDeskDbContext Db { get; } = db;
        public IAuthorizationService Authorization { get; } = authorization;
        public Ticket Ticket { get; } = ticket;
        public SessionUser Customer { get; } = customer;
        public SessionUser OtherCustomer { get; } = otherCustomer;
        public SessionUser Agent { get; } = agent;

        public static async Task<ChatFixture> CreateAsync(IServiceProvider services, bool assignedToAgent)
        {
            var db = services.GetRequiredService<CollabDeskDbContext>();
            var authorization = services.GetRequiredService<IAuthorizationService>();
            var users = await db.Users.Where(user => user.Email == AuthWebApplicationFactory.CustomerEmail ||
                    user.Email == AuthWebApplicationFactory.OtherCustomerEmail || user.Email == AuthWebApplicationFactory.AgentEmail)
                .ToDictionaryAsync(user => user.Email);
            var customer = await CreateSessionAsync(db, users[AuthWebApplicationFactory.CustomerEmail]);
            var otherCustomer = await CreateSessionAsync(db, users[AuthWebApplicationFactory.OtherCustomerEmail]);
            var agent = await CreateSessionAsync(db, users[AuthWebApplicationFactory.AgentEmail]);
            var ticket = new Ticket
            {
                CustomerId = customer.UserId,
                AssignedAgentId = assignedToAgent ? agent.UserId : null,
                Status = assignedToAgent ? TicketStatus.Claimed : TicketStatus.Open,
                Title = $"Chat fixture {Guid.NewGuid():N}",
                Description = "Fictional SignalR test ticket"
            };
            db.Tickets.Add(ticket);
            await db.SaveChangesAsync();
            return new ChatFixture(db, authorization, ticket, customer, otherCustomer, agent);
        }

        public ChatHub CreateHub(SessionUser user, RecordingClients clients, RecordingGroups groups, string connectionId) =>
            CreateHub(user, clients, groups, new TestHubCallerContext(user.Principal, connectionId));

        public ChatHub CreateHub(SessionUser user, RecordingClients clients, RecordingGroups groups, TestHubCallerContext context) => new(Db,
            Authorization, new TicketService(Db))
        {
            Context = context,
            Clients = clients,
            Groups = groups
        };

        private static async Task<SessionUser> CreateSessionAsync(CollabDeskDbContext db, AppUser user)
        {
            var session = new AuthSession { UserId = user.Id, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10) };
            db.AuthSessions.Add(session);
            await db.SaveChangesAsync();
            return new SessionUser(user.Id, session.Id, user.Role);
        }
    }

    private sealed record SessionUser(Guid UserId, Guid SessionId, Role Role)
    {
        public ClaimsPrincipal Principal => new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, UserId.ToString()),
            new Claim(ClaimTypes.Role, Role.ToString()),
            new Claim(ClaimNames.SessionId, SessionId.ToString())
        ], "test"));
    }

    private sealed class TestHubCallerContext : HubCallerContext
    {
        private readonly ClaimsPrincipal _principal;
        public TestHubCallerContext(ClaimsPrincipal principal, string connectionId)
        {
            _principal = principal;
            ConnectionId = connectionId;
        }
        public bool Aborted { get; private set; }
        public override string ConnectionId { get; }
        public override string? UserIdentifier => _principal.FindFirstValue(ClaimTypes.NameIdentifier);
        public override ClaimsPrincipal User => _principal;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() => Aborted = true;
    }

    private sealed record ClientCall(string Target, object?[] Args);

    private sealed class RecordingClientProxy(List<ClientCall> calls) : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            calls.Add(new ClientCall(method, args));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingClients : IHubCallerClients
    {
        private readonly List<ClientCall> _calls = [];
        private readonly IClientProxy _proxy;
        public RecordingClients() => _proxy = new RecordingClientProxy(_calls);
        public IReadOnlyList<ClientCall> Calls => _calls;
        public IClientProxy All => _proxy;
        public IClientProxy Caller => _proxy;
        public IClientProxy Others => _proxy;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => _proxy;
        public IClientProxy Client(string connectionId) => _proxy;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => _proxy;
        public IClientProxy Group(string groupName) => _proxy;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => _proxy;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => _proxy;
        public IClientProxy OthersInGroup(string groupName) => _proxy;
        public IClientProxy User(string userId) => _proxy;
        public IClientProxy Users(IReadOnlyList<string> userIds) => _proxy;
    }

    private sealed record GroupAddition(string ConnectionId, string Group);

    private sealed class RecordingGroups : IGroupManager
    {
        public List<GroupAddition> Additions { get; } = [];
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            Additions.Add(new GroupAddition(connectionId, groupName));
            return Task.CompletedTask;
        }
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
