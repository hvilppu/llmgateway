using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace LlmGateway;

// Rajapinta mahdollistaa mock-toteutuksen testeissä.
public interface IAzureOpenAIClient
{
    Task<ChatResponse> GetChatCompletionAsync(ChatRequest request, string requestId, string modelKey, string? systemPrompt = null, CancellationToken cancellationToken = default);
    Task<float[]> GetEmbeddingAsync(string text, CancellationToken cancellationToken = default);

    // Raaka chat completion -kutsu function calling -agenttilooppia varten.
    // messages: koko viestihistoria (role+content+tool_calls+tool_call_id).
    // tools: tool-määrittelyt JSON-objekteina (voi olla null).
    // Palauttaa AzureRawCompletion josta finish_reason kertoo onko vastaus valmis vai tarvitaanko tool calls.
    Task<AzureRawCompletion> GetRawCompletionAsync(
        IReadOnlyList<object> messages,
        IReadOnlyList<object>? tools,
        string modelKey,
        CancellationToken cancellationToken = default);
}

// Lähettää pyynnöt Azure OpenAI:lle. Sisältää retry-logiikan, per-kutsu timeoutin
// ja circuit breakerin vikaantuneen palvelun ohittamiseksi.
public class AzureOpenAIClient : IAzureOpenAIClient
{
    private readonly HttpClient _httpClient;
    private readonly AzureOpenAIOptions _options;
    private readonly ILogger<AzureOpenAIClient> _logger;
    private readonly ICircuitBreaker _circuitBreaker;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // HttpClient injektoidaan IHttpClientFactory:n kautta (rekisteröity AddHttpClient:llä).
    public AzureOpenAIClient(
        HttpClient httpClient,
        IOptions<AzureOpenAIOptions> options,
        ILogger<AzureOpenAIClient> logger,
        ICircuitBreaker circuitBreaker)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
        _circuitBreaker = circuitBreaker;

        _httpClient.BaseAddress = new Uri(_options.Endpoint);
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _options.ApiKey);
    }

    // Lähettää chat-pyynnön Azure OpenAI:lle. Tarkistaa circuit breakerin ensin,
    // sen jälkeen yrittää kutsua MaxRetries kertaa transienttien virheiden sattuessa.
    // Heittää CircuitBreakerOpenException jos piiri on auki, HttpRequestException muuten.
    public async Task<ChatResponse> GetChatCompletionAsync(ChatRequest request, string requestId, string modelKey, string? systemPrompt = null, CancellationToken cancellationToken = default)
    {
        // Haetaan Azure-deploymentName modelKeyn perusteella konfigista
        if (!_options.Deployments.TryGetValue(modelKey, out var deploymentName))
            throw new InvalidOperationException($"Unknown modelKey '{modelKey}' for AzureOpenAI");

        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["requestId"] = requestId,
            ["conversationId"] = request.ConversationId ?? string.Empty,
            ["modelKey"] = modelKey,
            ["deploymentName"] = deploymentName
        });

        if (_circuitBreaker.IsOpen(modelKey))
        {
            _logger.LogWarning("Circuit breaker is OPEN, rejecting request for {ModelKey}", modelKey);
            throw new CircuitBreakerOpenException(modelKey);
        }

        var url = $"/openai/deployments/{deploymentName}/chat/completions?api-version={_options.ApiVersion}";

        var messages = systemPrompt is not null
            ? new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user",   content = request.Message }
            }
            : new object[]
            {
                new { role = "user", content = request.Message }
            };

        var payload = new
        {
            messages,
            temperature = 0.2,
            max_tokens = 512
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        Exception? lastException = null;

        for (int attempt = 0; attempt <= _options.MaxRetries; attempt++)
        {
            var attemptNumber = attempt + 1;

            _logger.LogInformation("Sending Azure OpenAI request. Attempt={Attempt}, MessageLength={MessageLength}",
                attemptNumber, request.Message.Length);

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(_options.TimeoutMs);

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                using var response = await _httpClient.PostAsync(url, content, cts.Token);
                stopwatch.Stop();

                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "Azure OpenAI non-success status. StatusCode={StatusCode}, LatencyMs={Latency}, BodySnippet={BodySnippet}",
                        (int)response.StatusCode,
                        stopwatch.ElapsedMilliseconds,
                        responseBody.Length > 300 ? responseBody[..300] : responseBody);

                    if (IsTransient(response.StatusCode))
                    {
                        // Transientti virhe (408, 429, 5xx) — merkitään circuit breakerille
                        _circuitBreaker.RecordFailure(modelKey);

                        if (attempt < _options.MaxRetries)
                        {
                            var delay = TimeSpan.FromMilliseconds(_options.RetryDelayMs * (attempt + 1));
                            _logger.LogInformation("Transient error, will retry after {DelayMs} ms", delay.TotalMilliseconds);
                            await Task.Delay(delay, cancellationToken);
                            continue;
                        }
                    }
                    // 4xx client-virheet (400, 401, 403, 404) eivät kerro palvelimen tilasta —
                    // ei rekisteröidä circuit breakerille

                    throw new HttpRequestException($"Azure OpenAI error: {(int)response.StatusCode} {response.ReasonPhrase}");
                }

                _logger.LogInformation("Azure OpenAI call succeeded. LatencyMs={Latency}", stopwatch.ElapsedMilliseconds);

                var completion = JsonSerializer.Deserialize<AzureChatCompletionResponse>(responseBody, JsonOptions)
                                 ?? throw new InvalidOperationException("Failed to deserialize Azure OpenAI response");

                var firstMessage = completion.Choices.FirstOrDefault()?.Message?.Content ?? string.Empty;

                var usage = completion.Usage != null
                    ? new UsageInfo
                    {
                        PromptTokens = completion.Usage.Prompt_tokens,
                        CompletionTokens = completion.Usage.Completion_tokens,
                        TotalTokens = completion.Usage.Total_tokens
                    }
                    : null;

                _circuitBreaker.RecordSuccess(modelKey);

                return new ChatResponse
                {
                    Reply = firstMessage,
                    Model = completion.Model,
                    Usage = usage,
                    RequestId = requestId
                };
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Aikakatkaisu
                lastException = ex;
                _logger.LogWarning(ex, "Azure OpenAI request timed out. Attempt={Attempt}", attemptNumber);
                _circuitBreaker.RecordFailure(modelKey);

                if (attempt < _options.MaxRetries)
                {
                    var delay = TimeSpan.FromMilliseconds(_options.RetryDelayMs * (attempt + 1));
                    _logger.LogInformation("Timeout, will retry after {DelayMs} ms", delay.TotalMilliseconds);
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }

                break;
            }
            catch (Exception ex)
            {
                lastException = ex;
                _logger.LogError(ex, "Azure OpenAI request failed unexpectedly. Attempt={Attempt}", attemptNumber);
                _circuitBreaker.RecordFailure(modelKey);

                if (attempt < _options.MaxRetries && IsTransientException(ex))
                {
                    var delay = TimeSpan.FromMilliseconds(_options.RetryDelayMs * (attempt + 1));
                    _logger.LogInformation("Transient exception, will retry after {DelayMs} ms", delay.TotalMilliseconds);
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }

                break;
            }
        }

        throw lastException ?? new Exception("Azure OpenAI request failed with unknown error");
    }

    // Raaka chat completion -kutsu function calling -agenttilooppia varten.
    // Käyttää samaa retry/circuit-breaker -logiikkaa kuin GetChatCompletionAsync.
    public async Task<AzureRawCompletion> GetRawCompletionAsync(
        IReadOnlyList<object> messages,
        IReadOnlyList<object>? tools,
        string modelKey,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Deployments.TryGetValue(modelKey, out var deploymentName))
            throw new InvalidOperationException($"Unknown modelKey '{modelKey}' for AzureOpenAI");

        if (_circuitBreaker.IsOpen(modelKey))
        {
            _logger.LogWarning("Circuit breaker is OPEN, rejecting raw request for {ModelKey}", modelKey);
            throw new CircuitBreakerOpenException(modelKey);
        }

        var url = $"/openai/deployments/{deploymentName}/chat/completions?api-version={_options.ApiVersion}";

        var payloadObj = tools is { Count: > 0 }
            ? (object)new { messages, tools, temperature = 0.2, max_tokens = 1024 }
            : new { messages, temperature = 0.2, max_tokens = 1024 };

        var json = JsonSerializer.Serialize(payloadObj, JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        Exception? lastException = null;

        for (int attempt = 0; attempt <= _options.MaxRetries; attempt++)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(_options.TimeoutMs);

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                using var response = await _httpClient.PostAsync(url, content, cts.Token);
                stopwatch.Stop();

                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "Azure OpenAI non-success (raw). StatusCode={StatusCode}, LatencyMs={Latency}",
                        (int)response.StatusCode, stopwatch.ElapsedMilliseconds);

                    if (IsTransient(response.StatusCode))
                    {
                        // Transientti virhe (408, 429, 5xx) — merkitään circuit breakerille
                        _circuitBreaker.RecordFailure(modelKey);
                        if (attempt < _options.MaxRetries)
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(_options.RetryDelayMs * (attempt + 1)), cancellationToken);
                            continue;
                        }
                    }
                    // 4xx client-virheet eivät kerro palvelimen tilasta —
                    // ei rekisteröidä circuit breakerille

                    throw new HttpRequestException($"Azure OpenAI error: {(int)response.StatusCode} {response.ReasonPhrase}");
                }

                _logger.LogInformation("Azure OpenAI raw call succeeded. LatencyMs={Latency}", stopwatch.ElapsedMilliseconds);

                var completion = JsonSerializer.Deserialize<AzureChatCompletionResponse>(responseBody, JsonOptions)
                                 ?? throw new InvalidOperationException("Failed to deserialize Azure OpenAI response");

                var choice = completion.Choices.FirstOrDefault();
                _circuitBreaker.RecordSuccess(modelKey);

                return new AzureRawCompletion
                {
                    FinishReason = choice?.Finish_reason ?? "stop",
                    Content = choice?.Message?.Content,
                    ToolCalls = choice?.Message?.Tool_calls,
                    Usage = completion.Usage,
                    Model = completion.Model
                };
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                lastException = ex;
                _circuitBreaker.RecordFailure(modelKey);
                if (attempt < _options.MaxRetries)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(_options.RetryDelayMs * (attempt + 1)), cancellationToken);
                    continue;
                }
                break;
            }
            catch (Exception ex)
            {
                lastException = ex;
                _circuitBreaker.RecordFailure(modelKey);
                if (attempt < _options.MaxRetries && IsTransientException(ex))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(_options.RetryDelayMs * (attempt + 1)), cancellationToken);
                    continue;
                }
                break;
            }
        }

        throw lastException ?? new Exception("Azure OpenAI raw request failed with unknown error");
    }

    // Generoi embedding-vektori annetulle tekstille Azure OpenAI Embeddings API:lla.
    // Käytetään RAG-haussa: tekstistä luodaan vektori, jolla haetaan Cosmos DB:stä lähimmät dokumentit.
    public async Task<float[]> GetEmbeddingAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.EmbeddingDeployment))
            throw new InvalidOperationException("AzureOpenAI:EmbeddingDeployment is not configured");

        var url = $"/openai/deployments/{_options.EmbeddingDeployment}/embeddings?api-version={_options.ApiVersion}";
        var payload = new { input = text };
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_options.TimeoutMs);

        using var response = await _httpClient.PostAsync(url, content, cts.Token);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Embedding API error: {(int)response.StatusCode} {response.ReasonPhrase}");

        using var doc = JsonDocument.Parse(body);
        var values = doc.RootElement
            .GetProperty("data")[0]
            .GetProperty("embedding")
            .EnumerateArray()
            .Select(e => e.GetSingle())
            .ToArray();

        return values;
    }

    // 408 Timeout, 429 Rate limit ja 5xx ovat tilapäisiä — kannattaa yrittää uudelleen.
    private static bool IsTransient(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or >= HttpStatusCode.InternalServerError; // 500+
    }

    // TaskCanceledException tarkoittaa tässä kontekstissa omaa timeoutia (ei käyttäjän peruutusta).
    private static bool IsTransientException(Exception ex)
    {
        return ex is TaskCanceledException; // voidaan laajentaa esim. SocketExceptioniin jne.
    }
}