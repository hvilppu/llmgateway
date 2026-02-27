# LlmGateway

ASP.NET Core (.NET 10) minimal API gateway Azure OpenAI -palvelulle.

## Rakenne

```
Program.cs                DI-rekisteröinnit, middleware-pipeline
ChatEndpoints.cs          POST /api/chat endpoint — yksinkertainen + agenttiloop (function calling)
AzureOpenAIClient.cs      IAzureOpenAIClient + toteutus (retry, circuit breaker, GetRawCompletionAsync)
AzureOpenAIOptions.cs     Konfiguraatio-optiot (oma tiedosto)
CircuitBreaker.cs         ICircuitBreaker, InMemoryCircuitBreaker, CircuitBreakerOptions
Routing.cs                IRoutingEngine, RoutingEngine, PolicyOptions, PolicyConfig
RagService.cs             IRagService, CosmosRagService, CosmosRagOptions (vektorihaku)
QueryService.cs           IQueryService, CosmosQueryService (Text-to-NoSQL, SELECT-only)
Models/Models.cs          ChatRequest, ChatResponse, UsageInfo, Azure-vastausmallit + tool calling -mallit
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
  "EmbeddingDeployment": "text-embedding-3-small",
  "Deployments": {
    "gpt4": "YOUR-GPT4-DEPLOYMENT-NAME",
    "gpt4oMini": "YOUR-GPT4O-MINI-DEPLOYMENT-NAME"
  }
},
"Policies": {
  "chat_default": { "PrimaryModel": "gpt4oMini" },
  "critical":     { "PrimaryModel": "gpt4" },
  "tools":        { "PrimaryModel": "gpt4", "ToolsEnabled": true }
},
"CosmosRag": {
  "ConnectionString": "AccountEndpoint=...",
  "DatabaseName": "mydb",
  "ContainerName": "documents",
  "TopK": 5,
  "VectorField": "embedding",
  "ContentField": "content"
},
"CircuitBreaker": {
  "FailureThreshold": 5,
  "BreakDurationSeconds": 30
}
```

## Policy-pohjainen routing

`ChatRequest.Policy` määrittää käytettävän mallin ja toimintatavan:
- `null` / puuttuu → `chat_default` → `gpt4oMini`, yksinkertainen kutsu
- `"critical"` → `gpt4`, yksinkertainen kutsu fallback-ketjulla
- `"tools"` → `gpt4`, function calling -agenttiloop (search_documents + query_database)

`RoutingEngine.ResolveModelChain` palauttaa [primary, fallback1, ...] -listan.
`RoutingEngine.IsToolsEnabled` kertoo aktivoiko policy agenttilooppin.

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
curl -X POST http://localhost:5079/api/chat -H "Content-Type: application/json" -d "{\"message\": \"Hei\"}"

# critical policy (gpt4)
curl -X POST http://localhost:5079/api/chat -H "Content-Type: application/json" -d "{\"message\": \"Analysoi tämä\", \"policy\": \"critical\"}"

# tools policy — LLM valitsee itse search_documents tai query_database
curl -X POST http://localhost:5079/api/chat -H "Content-Type: application/json" -d "{\"message\": \"Mikä oli lämpötilan keskiarvo Helsingissä helmikuussa 2025?\", \"policy\": \"tools\"}"
```

OpenAPI-schema: `http://localhost:5079/openapi/v1.json`


## Flow

### Yksinkertainen kutsu (policy: "chat_default" tai "critical")
1. POST /api/chat `{ "message": "Kerro vitsi", "policy": "critical" }`
2. `ChatEndpoints` → `RoutingEngine.ResolveModelChain` → `["gpt4"]`
3. `IsToolsEnabled` → false → `HandleSimpleAsync`
4. `AzureOpenAIClient.GetChatCompletionAsync` — circuit breaker + retry
5. Vastaus: `{ "reply": "...", "model": "gpt-4", "usage": {...}, "requestId": "..." }`

### Function calling -agenttiloop (policy: "tools")
1. POST /api/chat `{ "message": "Mikä oli keskilämpötila Helsingissä helmikuussa?", "policy": "tools" }`
2. `ChatEndpoints` → `IsToolsEnabled` → true → `HandleWithToolsAsync`
3. Rakennetaan messages-lista + tool-määrittelyt (search_documents, query_database)
4. `AzureOpenAIClient.GetRawCompletionAsync(messages, tools, "gpt4")`
5. Azure palauttaa `finish_reason: "tool_calls"` → `query_database(sql="SELECT AVG(...)")`
6. `CosmosQueryService.ExecuteQueryAsync(sql)` → JSON-tulokset
7. Lisätään tool-tulos messages-listaan, loop uudelleen
8. Azure palauttaa `finish_reason: "stop"` → lopullinen vastaus
9. Vastaus: `{ "reply": "Helmikuussa 2025 keskilämpötila Helsingissä oli -3.2°C.", ... }`

## Git
Important: I make commits manyally DO NOT EVER DO COMMITS
