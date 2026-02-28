using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LlmGateway.Tests;

public class AzureOpenAIClientTests
{
    private static AzureOpenAIOptions BaseOptions(int maxRetries = 0) => new()
    {
        Endpoint = "https://fake.openai.azure.com/",
        ApiKey = "test-key",
        ApiVersion = "2024-02-15-preview",
        MaxRetries = maxRetries,
        RetryDelayMs = 0,
        TimeoutMs = 5000,
        Deployments = new Dictionary<string, string> { { "gpt4oMini", "gpt4o-mini-deployment" } }
    };

    private static AzureOpenAIClient CreateClient(
        FakeHttpMessageHandler handler,
        FakeCircuitBreaker circuitBreaker,
        AzureOpenAIOptions? options = null)
    {
        var httpClient = new HttpClient(handler);
        return new AzureOpenAIClient(
            httpClient,
            Options.Create(options ?? BaseOptions()),
            NullLogger<AzureOpenAIClient>.Instance,
            circuitBreaker);
    }

    private static HttpResponseMessage SuccessResponse(string reply = "Hei!") =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent($$"""
            {
                "id": "cmpl-123",
                "model": "gpt-4",
                "choices": [{
                    "index": 0,
                    "message": { "role": "assistant", "content": "{{reply}}" },
                    "finish_reason": "stop"
                }],
                "usage": {
                    "prompt_tokens": 10,
                    "completion_tokens": 5,
                    "total_tokens": 15
                }
            }
            """, Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage ErrorResponse(HttpStatusCode statusCode) =>
        new(statusCode) { Content = new StringContent("error", Encoding.UTF8, "application/json") };

    private static HttpResponseMessage ToolCallResponse(string toolCallId = "call-1", string functionName = "query_database", string arguments = "{\"sql\":\"SELECT 1\"}") =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent($$"""
            {
                "id": "cmpl-456",
                "model": "gpt-4",
                "choices": [{
                    "index": 0,
                    "message": {
                        "role": "assistant",
                        "content": null,
                        "tool_calls": [{
                            "id": "{{toolCallId}}",
                            "type": "function",
                            "function": {
                                "name": "{{functionName}}",
                                "arguments": "{{arguments}}"
                            }
                        }]
                    },
                    "finish_reason": "tool_calls"
                }],
                "usage": { "prompt_tokens": 20, "completion_tokens": 10, "total_tokens": 30 }
            }
            """, Encoding.UTF8, "application/json")
        };

    [Fact]
    public async Task GetChatCompletion_WhenCircuitBreakerOpen_ThrowsCircuitBreakerOpenException()
    {
        var handler = new FakeHttpMessageHandler();
        var cb = new FakeCircuitBreaker { IsOpenResult = true };
        var client = CreateClient(handler, cb);

        await Assert.ThrowsAsync<CircuitBreakerOpenException>(() =>
            client.GetChatCompletionAsync(new ChatRequest { Message = "Hi" }, "req-1", "gpt4oMini"));

        Assert.Equal(0, handler.CallCount); // HTTP call never made
    }

    [Fact]
    public async Task GetChatCompletion_UnknownModelKey_ThrowsInvalidOperationException()
    {
        var handler = new FakeHttpMessageHandler();
        var cb = new FakeCircuitBreaker();
        var client = CreateClient(handler, cb);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetChatCompletionAsync(new ChatRequest { Message = "Hi" }, "req-1", "unknownKey"));
    }

    [Fact]
    public async Task GetChatCompletion_Success_ReturnsChatResponse()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(SuccessResponse("Moi!"));
        var cb = new FakeCircuitBreaker();
        var client = CreateClient(handler, cb);

        var result = await client.GetChatCompletionAsync(
            new ChatRequest { Message = "Hei" }, "req-42", "gpt4oMini");

        Assert.Equal("Moi!", result.Reply);
        Assert.Equal("gpt-4", result.Model);
        Assert.Equal("req-42", result.RequestId);
        Assert.NotNull(result.Usage);
        Assert.Equal(10, result.Usage!.PromptTokens);
        Assert.Equal(5, result.Usage.CompletionTokens);
        Assert.Equal(15, result.Usage.TotalTokens);
    }

    [Fact]
    public async Task GetChatCompletion_Success_RecordsSuccess()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(SuccessResponse());
        var cb = new FakeCircuitBreaker();
        var client = CreateClient(handler, cb);

        await client.GetChatCompletionAsync(new ChatRequest { Message = "Hi" }, "req-1", "gpt4oMini");

        Assert.Equal(1, cb.SuccessCount);
        Assert.Equal(0, cb.FailureCount);
    }

    [Fact]
    public async Task GetChatCompletion_TransientError429_RetriesAndRecordsFailures()
    {
        // MaxRetries=2 → 3 HTTP calls.
        // RecordFailure kutsutaan kerran per yritys IsTransient-haarassa.
        // Viimeinen yritys: RecordFailure + break → lastException heitetään loopin ulkopuolelta,
        // ei jää kiinni outer catch-lohkoon → ei ylimääräistä RecordFailure-kutsua.
        // Total = 3.
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(ErrorResponse(HttpStatusCode.TooManyRequests));
        handler.Enqueue(ErrorResponse(HttpStatusCode.TooManyRequests));
        handler.Enqueue(ErrorResponse(HttpStatusCode.TooManyRequests));
        var cb = new FakeCircuitBreaker();
        var client = CreateClient(handler, cb, BaseOptions(maxRetries: 2));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetChatCompletionAsync(new ChatRequest { Message = "Hi" }, "req-1", "gpt4oMini"));

        Assert.Equal(3, handler.CallCount);
        Assert.Equal(3, cb.FailureCount); // yksi per yritys
    }

    // ===== GetRawCompletionAsync =====

    [Fact]
    public async Task GetRawCompletion_StopResponse_ReturnsFinishReasonStop()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(SuccessResponse("Vastaus"));
        var cb = new FakeCircuitBreaker();
        var client = CreateClient(handler, cb);

        var messages = new List<object> { new { role = "user", content = "Hei" } };
        var result = await client.GetRawCompletionAsync(messages, null, "gpt4oMini");

        Assert.Equal("stop", result.FinishReason);
        Assert.Equal("Vastaus", result.Content);
        Assert.Null(result.ToolCalls);
        Assert.Equal("gpt-4", result.Model);
        Assert.Equal(15, result.Usage?.Total_tokens);
    }

    [Fact]
    public async Task GetRawCompletion_ToolCallResponse_ReturnsToolCalls()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(ToolCallResponse("call-1", "query_database", "{\\\"sql\\\":\\\"SELECT 1\\\"}"));
        var cb = new FakeCircuitBreaker();
        var client = CreateClient(handler, cb);

        var messages = new List<object> { new { role = "user", content = "Laske keskiarvo" } };
        var result = await client.GetRawCompletionAsync(messages, null, "gpt4oMini");

        Assert.Equal("tool_calls", result.FinishReason);
        Assert.Null(result.Content);
        Assert.NotNull(result.ToolCalls);
        Assert.Single(result.ToolCalls!);
        Assert.Equal("call-1", result.ToolCalls![0].Id);
        Assert.Equal("query_database", result.ToolCalls[0].Function.Name);
    }

    [Fact]
    public async Task GetRawCompletion_WhenCircuitBreakerOpen_ThrowsCircuitBreakerOpenException()
    {
        var handler = new FakeHttpMessageHandler();
        var cb = new FakeCircuitBreaker { IsOpenResult = true };
        var client = CreateClient(handler, cb);

        var messages = new List<object> { new { role = "user", content = "test" } };
        await Assert.ThrowsAsync<CircuitBreakerOpenException>(() =>
            client.GetRawCompletionAsync(messages, null, "gpt4oMini"));

        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task GetChatCompletion_NonTransientError_NoRetry()
    {
        // 400 Bad Request is not transient — should not retry and must not open circuit breaker.
        // Client errors (4xx) indicate a bad request, not a server health issue.
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(ErrorResponse(HttpStatusCode.BadRequest));
        var cb = new FakeCircuitBreaker();
        var client = CreateClient(handler, cb, BaseOptions(maxRetries: 2));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetChatCompletionAsync(new ChatRequest { Message = "Hi" }, "req-1", "gpt4oMini"));

        Assert.Equal(1, handler.CallCount); // no retries
        Assert.Equal(0, cb.FailureCount);   // client error — circuit breaker must not record failure
    }
}
