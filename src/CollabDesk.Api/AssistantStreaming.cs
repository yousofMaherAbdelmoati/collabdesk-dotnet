using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CollabDesk.Api;

/// <summary>Limits applied independently of the configured assistant provider.</summary>
public sealed class AssistantStreamOptions
{
    public const string SectionName = "AssistantStream";
    public int MaxTokens { get; init; } = 32;
    public int MaxOutputCharacters { get; init; } = 4_096;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>
    /// Optional local-demo pacing between tokens. This is disabled by default so
    /// non-development hosts do not add artificial response latency.
    /// </summary>
    public int InterTokenDelayMilliseconds { get; init; }
}

public sealed record AssistantGenerationRequest(long TicketId, int MaxTokens);
public sealed record AssistantGenerationToken(string Text);

/// <summary>
/// Produces assistant output without coupling HTTP/SSE transport to a particular model vendor.
/// Implementations must observe the supplied cancellation token.
/// </summary>
public interface IAssistantStreamProvider
{
    IAsyncEnumerable<AssistantGenerationToken> GenerateAsync(AssistantGenerationRequest request, CancellationToken cancellationToken);
}

/// <summary>A deterministic, local provider suitable for development and tests; it uses no external model or API key.</summary>
public sealed class DeterministicAssistantStreamProvider(IOptions<AssistantStreamOptions> options) : IAssistantStreamProvider
{
    private readonly int _interTokenDelayMilliseconds = options.Value.InterTokenDelayMilliseconds;

    public async IAsyncEnumerable<AssistantGenerationToken> GenerateAsync(
        AssistantGenerationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var response = $"Demo stream only. Ticket {request.TicketId} is selected. This fixed local response exists solely to demonstrate SSE delivery and cancellation. A support agent will provide next steps.";
        var words = response.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < words.Length && index < request.MaxTokens; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new AssistantGenerationToken(index == words.Length - 1 ? words[index] : words[index] + " ");
            // Local development can pace the deterministic stream so a browser can exercise
            // cancellation against a real in-flight HTTP response. Production defaults to no
            // artificial delay.
            if (_interTokenDelayMilliseconds > 0)
                await Task.Delay(_interTokenDelayMilliseconds, cancellationToken);
            else
                await Task.Yield();
        }
    }
}

public static class AssistantSseWriter
{
    public static async Task WriteEventAsync(HttpResponse response, string eventName, object payload, CancellationToken cancellationToken)
    {
        var data = JsonSerializer.Serialize(payload);
        await response.WriteAsync($"event: {eventName}\ndata: {data}\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }
}

public sealed class AssistantStreamService(
    IAssistantStreamProvider provider,
    IOptions<AssistantStreamOptions> options,
    ILogger<AssistantStreamService> logger)
{
    private readonly AssistantStreamOptions _options = options.Value;

    public async Task StreamAsync(HttpContext context, long ticketId, CancellationToken requestCancellationToken)
    {
        var response = context.Response;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers.Append("X-Accel-Buffering", "no");

        using var timeout = new CancellationTokenSource(_options.Timeout);
        using var generationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            requestCancellationToken, context.RequestAborted, timeout.Token);
        var chunks = 0;
        var outputCharacters = 0;

        try
        {
            await foreach (var token in provider.GenerateAsync(
                new AssistantGenerationRequest(ticketId, _options.MaxTokens), generationCancellation.Token)
                .WithCancellation(generationCancellation.Token))
            {
                if (chunks >= _options.MaxTokens || token.Text.Length > _options.MaxOutputCharacters - outputCharacters)
                {
                    logger.LogWarning(new EventId(5003, "AssistantOutputLimit"),
                        "Assistant stream exceeded output bounds for ticket {TicketId}; trace {TraceId}; chunks {Chunks}; characters {Characters}; provider {Provider}",
                        ticketId, context.TraceIdentifier, chunks, outputCharacters, provider.GetType().Name);
                    await AssistantSseWriter.WriteEventAsync(response, "error", new { code = "output_limit_exceeded", message = "Assistant output exceeded the configured limit." }, requestCancellationToken);
                    return;
                }

                await AssistantSseWriter.WriteEventAsync(response, "token", new { sequence = chunks, text = token.Text }, requestCancellationToken);
                chunks++;
                outputCharacters += token.Text.Length;
            }

            await AssistantSseWriter.WriteEventAsync(response, "complete", new { chunks, outputCharacters }, requestCancellationToken);
            logger.LogInformation(new EventId(5001, "AssistantStreamComplete"),
                "Assistant stream completed for ticket {TicketId}; trace {TraceId}; chunks {Chunks}; characters {Characters}; provider {Provider}",
                ticketId, context.TraceIdentifier, chunks, outputCharacters, provider.GetType().Name);
        }
        catch (OperationCanceledException) when (requestCancellationToken.IsCancellationRequested || context.RequestAborted.IsCancellationRequested)
        {
            logger.LogInformation(new EventId(5004, "AssistantClientDisconnected"),
                "Assistant stream canceled by client for ticket {TicketId}; trace {TraceId}; chunks {Chunks}; provider {Provider}",
                ticketId, context.TraceIdentifier, chunks, provider.GetType().Name);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            logger.LogWarning(new EventId(5002, "AssistantStreamTimeout"),
                "Assistant stream timed out for ticket {TicketId}; trace {TraceId}; chunks {Chunks}; provider {Provider}",
                ticketId, context.TraceIdentifier, chunks, provider.GetType().Name);
            await AssistantSseWriter.WriteEventAsync(response, "error", new { code = "timeout", message = "Assistant generation timed out." }, requestCancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(new EventId(5000, "AssistantProviderFailure"), exception,
                "Assistant provider failed for ticket {TicketId}; trace {TraceId}; chunks {Chunks}; provider {Provider}",
                ticketId, context.TraceIdentifier, chunks, provider.GetType().Name);
            await AssistantSseWriter.WriteEventAsync(response, "error", new { code = "provider_failure", message = "Assistant generation failed." }, requestCancellationToken);
        }
    }
}
