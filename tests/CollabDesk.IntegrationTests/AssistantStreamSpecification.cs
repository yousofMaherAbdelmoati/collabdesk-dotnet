using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CollabDesk.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CollabDesk.IntegrationTests;

public sealed class AssistantStreamSpecification(AuthWebApplicationFactory factory) : IClassFixture<AuthWebApplicationFactory>
{
    [Fact]
    public async Task Concurrent_streams_emit_ordered_tokens_and_complete_frames()
    {
        using var client = factory.CreateSecureClient();
        var csrf = await LoginAsync(client);
        var ticket = await PostJsonWithCsrfAsync(client, "/api/tickets", new { title = "SSE fixture", description = "Fictional local test ticket" }, csrf, $"sse-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.Created, ticket.StatusCode);
        var ticketId = (await ticket.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();

        var first = PostJsonWithCsrfAsync(client, "/api/assistant/stream", new { ticketId }, csrf);
        var second = PostJsonWithCsrfAsync(client, "/api/assistant/stream", new { ticketId }, csrf);
        var responses = await Task.WhenAll(first, second);

        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
            var frames = ParseFrames(await response.Content.ReadAsStringAsync());
            var tokens = frames.Where(frame => frame.Event == "token").ToArray();
            Assert.NotEmpty(tokens);
            Assert.Equal(Enumerable.Range(0, tokens.Length), tokens.Select(frame => frame.Data.GetProperty("sequence").GetInt32()));
            Assert.Equal("complete", frames[^1].Event);
            Assert.Equal(tokens.Length, frames[^1].Data.GetProperty("chunks").GetInt32());
            Assert.DoesNotContain(frames, frame => frame.Event == "error");
        }
    }

    [Fact]
    public async Task Provider_failure_is_rendered_as_a_safe_error_frame()
    {
        var output = await StreamWithAsync(new ThrowingProvider(), new AssistantStreamOptions());

        var frames = ParseFrames(output);
        Assert.Single(frames);
        Assert.Equal("error", frames[0].Event);
        Assert.Equal("provider_failure", frames[0].Data.GetProperty("code").GetString());
        Assert.DoesNotContain("provider secret", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancellation_reaches_the_provider_and_releases_the_stream()
    {
        var provider = new WaitingProvider();
        using var canceled = new CancellationTokenSource();
        var context = NewSseContext(canceled.Token);
        var service = CreateService(provider, new AssistantStreamOptions { Timeout = TimeSpan.FromSeconds(5) });

        var streaming = service.StreamAsync(context, 101, canceled.Token);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        canceled.Cancel();
        await streaming.WaitAsync(TimeSpan.FromSeconds(1));
        await provider.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Timeout_and_output_limits_produce_bounded_error_frames()
    {
        var timeoutOutput = await StreamWithAsync(new WaitingProvider(), new AssistantStreamOptions { Timeout = TimeSpan.FromMilliseconds(25) });
        var timeoutFrame = Assert.Single(ParseFrames(timeoutOutput));
        Assert.Equal("error", timeoutFrame.Event);
        Assert.Equal("timeout", timeoutFrame.Data.GetProperty("code").GetString());

        var limitOutput = await StreamWithAsync(new OversizedProvider(), new AssistantStreamOptions { MaxOutputCharacters = 4 });
        var limitFrame = Assert.Single(ParseFrames(limitOutput));
        Assert.Equal("error", limitFrame.Event);
        Assert.Equal("output_limit_exceeded", limitFrame.Data.GetProperty("code").GetString());
    }

    private static async Task<string> StreamWithAsync(IAssistantStreamProvider provider, AssistantStreamOptions options)
    {
        var context = NewSseContext(CancellationToken.None);
        await CreateService(provider, options).StreamAsync(context, 101, CancellationToken.None);
        context.Response.Body.Position = 0;
        return await new StreamReader(context.Response.Body).ReadToEndAsync();
    }

    private static AssistantStreamService CreateService(IAssistantStreamProvider provider, AssistantStreamOptions options) =>
        new(provider, Options.Create(options), NullLogger<AssistantStreamService>.Instance);

    private static DefaultHttpContext NewSseContext(CancellationToken cancellationToken)
    {
        var context = new DefaultHttpContext { TraceIdentifier = "test-trace", RequestAborted = cancellationToken };
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static IReadOnlyList<SseFrame> ParseFrames(string output) => output.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
        .Select(frame => frame.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        .Select(lines => new SseFrame(
            lines.Single(line => line.StartsWith("event: ", StringComparison.Ordinal))[7..],
            JsonDocument.Parse(lines.Single(line => line.StartsWith("data: ", StringComparison.Ordinal))[6..]).RootElement.Clone()))
        .ToArray();

    private static async Task<string> LoginAsync(HttpClient client)
    {
        var login = await PostJsonWithCsrfAsync(client, "/api/auth/login", new { email = AuthWebApplicationFactory.CustomerEmail, password = AuthWebApplicationFactory.Password });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        return csrf.GetProperty("token").GetString()!;
    }

    private static async Task<HttpResponseMessage> PostJsonWithCsrfAsync(HttpClient client, string path, object body, string? csrf = null, string? idempotencyKey = null)
    {
        csrf ??= (await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf")).GetProperty("token").GetString();
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(request);
    }

    private sealed record SseFrame(string Event, JsonElement Data);

    private sealed class ThrowingProvider : IAssistantStreamProvider
    {
        public async IAsyncEnumerable<AssistantGenerationToken> GenerateAsync(AssistantGenerationRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            if (request.TicketId > 0) throw new InvalidOperationException("provider secret must not reach the client");
            yield break;
        }
    }

    private sealed class WaitingProvider : IAssistantStreamProvider
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<AssistantGenerationToken> GenerateAsync(AssistantGenerationRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult(true);
                throw;
            }
            yield break;
        }
    }

    private sealed class OversizedProvider : IAssistantStreamProvider
    {
        public async IAsyncEnumerable<AssistantGenerationToken> GenerateAsync(AssistantGenerationRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return new AssistantGenerationToken("too large");
        }
    }
}
