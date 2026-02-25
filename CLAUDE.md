# LlmGateway

ASP.NET Core (.NET 10) minimal API gateway Azure OpenAI -palvelulle.

## Rakenne

```
Program.cs                DI-rekisteröinnit, middleware-pipeline
ChatEndpoints.cs          POST /api/chat endpoint (extension method)
AzureOpenAIClient.cs      IAzureOpenAIClient + toteutus (retry, circuit breaker)
AzureOpenAIOptions.cs     Konfiguraatio-optiot (oma tiedosto)
CircuitBreaker.cs         ICircuitBreaker, InMemoryCircuitBreaker, CircuitBreakerOptions
Models/Models.cs          ChatRequest, ChatResponse, UsageInfo, Azure-vastausmallit
```

## Teknologiat

- .NET 10 minimal API
- Sisäänrakennettu OpenAPI (`AddOpenApi` / `MapOpenApi`) — ei Swashbuckle
- `IOptions<T>` konfiguraatiolle
- `IHttpClientFactory` / typed client HttpClient-hallintaan
- Circuit breaker in-memory toteutuksella

## Konfiguraatio (appsettings)

```json
"AzureOpenAI": {
  "Endpoint": "https://YOUR-RESOURCE.openai.azure.com/",
  "ApiKey": "...",
  "DeploymentName": "...",
  "ApiVersion": "2024-02-15-preview",
  "TimeoutMs": 15000,
  "MaxRetries": 2,
  "RetryDelayMs": 500
}
```

## Resilientti kutsulogiikka (AzureOpenAIClient)

- **Retry**: `MaxRetries` uudelleenyritystä eksponentiaalisella viiveellä (`RetryDelayMs * attempt`)
- **Timeout**: per-kutsu timeout `CancellationTokenSource.CancelAfter`
- **Circuit breaker**: `ICircuitBreaker` injektoitu — avautuu `FailureThreshold` virheen jälkeen, sulkeutuu `BreakDurationSeconds` kuluttua
- Transientit virheet: 408, 429, 5xx + `TaskCanceledException`

## Konventiot

- Endpointit omiin tiedostoihin `MapXxxEndpoints(this WebApplication app)` -patternilla
- Mallit `Models/`-kansiossa, namespace `LlmGateway` (ei `LlmGateway.Models`)
- Azure-vastausmallit (`AzureXxx`) erillään gatewayn omista malleista
- Lokitus structured logging -tyylillä (`Attempt=`, `LatencyMs=`, `TotalTokens=` jne.)

## Testaus

```bash
curl -X POST http://localhost:5079/api/chat \
  -H "Content-Type: application/json" \
  -d "{\"message\": \"Hei\"}"
```

OpenAPI-schema: `http://localhost:5079/openapi/v1.json`
