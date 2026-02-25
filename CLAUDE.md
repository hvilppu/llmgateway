# LlmGateway

ASP.NET Core (.NET 10) minimal API gateway Azure OpenAI -palvelulle.

## Rakenne

```
Program.cs                DI-rekisteröinnit, middleware-pipeline
ChatEndpoints.cs          POST /api/chat endpoint (extension method)
AzureOpenAIClient.cs      IAzureOpenAIClient + toteutus (retry, circuit breaker)
AzureOpenAIOptions.cs     Konfiguraatio-optiot (oma tiedosto)
CircuitBreaker.cs         ICircuitBreaker, InMemoryCircuitBreaker, CircuitBreakerOptions
Routing.cs                IRoutingEngine, RoutingEngine, PolicyOptions, PolicyConfig
Models/Models.cs          ChatRequest, ChatResponse, UsageInfo, Azure-vastausmallit
```

## Teknologiat

- .NET 10 minimal API
- Sisäänrakennettu OpenAPI (`AddOpenApi` / `MapOpenApi`) — ei Swashbuckle
- `IOptions<T>` konfiguraatiolle
- `IHttpClientFactory` / typed client HttpClient-hallintaan
- Circuit breaker in-memory toteutuksella
- Policy-pohjainen model routing

## Konfiguraatio (appsettings)

```json
"AzureOpenAI": {
  "Endpoint": "https://YOUR-RESOURCE-NAME.openai.azure.com/",
  "ApiKey": "...",
  "ApiVersion": "2024-02-15-preview",
  "TimeoutMs": 15000,
  "MaxRetries": 2,
  "RetryDelayMs": 500,
  "Deployments": {
    "gpt4": "YOUR-GPT4-DEPLOYMENT-NAME",
    "gpt4oMini": "YOUR-GPT4O-MINI-DEPLOYMENT-NAME"
  }
},
"Policies": {
  "chat_default": { "PrimaryModel": "gpt4oMini" },
  "critical":     { "PrimaryModel": "gpt4" }
},
"CircuitBreaker": {
  "FailureThreshold": 5,
  "BreakDurationSeconds": 30
}
```

## Policy-pohjainen routing

`ChatRequest.Policy` määrittää käytettävän mallin:
- `null` / puuttuu → `chat_default` → `gpt4oMini`
- `"critical"` → `gpt4`

`RoutingEngine.ResolveModelKey` hakee policyn konfigista ja palauttaa `modelKey`:n.
`AzureOpenAIClient` hakee `modelKey`:llä `deploymentName`:n ja tekee kutsun.

## Resilientti kutsulogiikka (AzureOpenAIClient)

- **Retry**: `MaxRetries` uudelleenyritystä eksponentiaalisella viiveellä
- **Timeout**: per-kutsu timeout `CancellationTokenSource.CancelAfter`
- **Circuit breaker**: avautuu `FailureThreshold` virheen jälkeen, sulkeutuu `BreakDurationSeconds` kuluttua
- Transientit virheet: 408, 429, 5xx + `TaskCanceledException`

## HTTP-vastaukset

| Tilanne | Statuskoodi |
|---------|-------------|
| Onnistui | 200 OK |
| Circuit breaker auki | 503 Service Unavailable |
| Azure-virhe | 502 Bad Gateway |
| Odottamaton virhe | 500 Internal Server Error |

## Konventiot

- Endpointit omiin tiedostoihin `MapXxxEndpoints(this WebApplication app)` -patternilla
- Mallit `Models/`-kansiossa, namespace `LlmGateway` (ei `LlmGateway.Models`)
- Lokitus structured logging -tyylillä (`Policy=`, `ModelKey=`, `LatencyMs=` jne.)

## Testaus

```bash
# chat_default policy (gpt4oMini)
curl -X POST http://localhost:5079/api/chat \
  -H "Content-Type: application/json" \
  -d "{\"message\": \"Hei\"}"

# critical policy (gpt4)
curl -X POST http://localhost:5079/api/chat \
  -H "Content-Type: application/json" \
  -d "{\"message\": \"Analysoi tämä\", \"policy\": \"critical\"}"
```

OpenAPI-schema: `http://localhost:5079/openapi/v1.json`


## Flow (Päivitä ohjelman edistyessä)
1. Pyyntö saapuu                                                                                                                      POST /api/chat                                                                                                                      
  { "message": "Kerro vitsi", "policy": "critical" }                                                                                                                                                                                                                        
  2. ChatEndpoints.cs — endpoint ottaa pyynnön vastaan
  - ASP.NET Core deserializoi JSON:n ChatRequest-objektiksi
  - Luodaan requestId (httpContext.TraceIdentifier)
  - Lokitetaan pyyntö

  3. RoutingEngine.ResolveModelKey — valitaan malli
  - Luetaan request.Policy → "critical"
  - Haetaan Policies["critical"] konfigista → PrimaryModel = "gpt4"
  - Palautetaan "gpt4"

  4. AzureOpenAIClient.GetChatCompletionAsync — tehdään kutsu
  - Tarkistetaan circuit breaker: IsOpen("gpt4") → false → jatketaan
  - Haetaan Deployments["gpt4"] → oikea Azure deployment name
  - Rakennetaan HTTP POST Azure OpenAI REST API:lle
  - Per-kutsu timeout käynnistetään (CancelAfter(15000))

  5. Azure OpenAI vastaa
  - Onnistui → RecordSuccess("gpt4"), palautetaan ChatResponse
  - Epäonnistui (429/5xx) → RecordFailure("gpt4"), retry viiveen jälkeen

  6. Vastaus takaisin asiakkaalle
  {
    "reply": "Miksi ohjelmoija ei pidä luonnosta? Koska siellä on liikaa ötököitä (bugeja). Olipas hyvä vitsi!.",
    "model": "gpt-4",
    "usage": { "promptTokens": 12, "completionTokens": 24, "totalTokens": 36 },
    "requestId": "0HN..."
  }
