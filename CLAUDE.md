# LlmGateway

ASP.NET Core (.NET 10) minimal API gateway Azure OpenAI -palvelulle. Tukee sekä Cosmos DB- että MS SQL -taustakantoja function calling -agenttiloopissa.

## Rakenne

```
Program.cs                              DI-rekisteröinnit, middleware-pipeline

Endpoints/
  ChatEndpoints.cs                      POST /api/chat endpoint — yksinkertainen + agenttiloop (function calling)
                                        Erillinen system prompt ja tool-kuvaus Cosmos DB- ja MS SQL -poluille

Infrastructure/
  AzureOpenAIClient.cs                  IAzureOpenAIClient + toteutus (retry, circuit breaker, GetRawCompletionAsync)
  AzureOpenAIOptions.cs                 Konfiguraatio-optiot
  CircuitBreaker.cs                     ICircuitBreaker, InMemoryCircuitBreaker, CircuitBreakerOptions

Middleware/
  ApiKeyMiddleware.cs                   X-Api-Key -headerin tarkistus, ApiKeyOptions

Models/
  Models.cs                             ChatRequest, ChatResponse, UsageInfo, Azure-vastausmallit + tool calling -mallit

Routing/
  Routing.cs                            IRoutingEngine, RoutingEngine, PolicyOptions, PolicyConfig
                                        PolicyConfig.QueryBackend ("cosmos" | "mssql")
                                        IRoutingEngine.GetQueryBackend(request)

Services/
  QueryService.cs                       IQueryService
                                        CosmosQueryService — Cosmos DB NoSQL SELECT -kyselyt
                                        SqlQueryService    — MS SQL / Azure SQL T-SQL SELECT -kyselyt
                                        CosmosOptions      — Cosmos DB -yhteysasetukset
                                        SqlOptions         — MS SQL -yhteysasetukset
```

### Namespacet

| Kansio | Namespace |
|--------|-----------|
| `Endpoints/` | `LlmGateway.Endpoints` |
| `Infrastructure/` | `LlmGateway.Infrastructure` |
| `Middleware/` | `LlmGateway.Middleware` |
| `Models/` | `LlmGateway.Models` |
| `Routing/` | `LlmGateway.Routing` |
| `Services/` | `LlmGateway.Services` |

## Teknologiat

- .NET 10 minimal API
- Sisäänrakennettu OpenAPI (`AddOpenApi` / `MapOpenApi`) — ei Swashbuckle
- `IOptions<T>` konfiguraatiolle
- `IHttpClientFactory` / typed client HttpClient-hallintaan
- Circuit breaker in-memory toteutuksella
- Policy-pohjainen model routing
- `Microsoft.Data.SqlClient` MS SQL -kyselyihin
- Keyed DI (`AddKeyedSingleton`) — molemmat query-backendit aktiivisena samanaikaisesti

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
  "critical":     { "PrimaryModel": "gpt4" },
  "tools":        { "PrimaryModel": "gpt4", "ToolsEnabled": true, "QueryBackend": "cosmos" },
  "tools_sql":    { "PrimaryModel": "gpt4", "ToolsEnabled": true, "QueryBackend": "mssql" }
},
"CosmosRag": {
  "ConnectionString": "AccountEndpoint=...",
  "DatabaseName": "ragdb",
  "ContainerName": "documents"
},
"Sql": {
  "ConnectionString": "Server=...;Database=...;..."
},
"CircuitBreaker": {
  "FailureThreshold": 5,
  "BreakDurationSeconds": 30
}
```

## Policy-pohjainen routing

`ChatRequest.Policy` määrittää käytettävän mallin, toimintatavan ja query-backendin:
- `null` / puuttuu → `chat_default` → `gpt4oMini`, yksinkertainen kutsu
- `"critical"` → `gpt4`, yksinkertainen kutsu fallback-ketjulla
- `"tools"` → `gpt4`, function calling -agenttiloop, Cosmos DB -backend
- `"tools_sql"` → `gpt4`, function calling -agenttiloop, MS SQL -backend

`RoutingEngine.ResolveModelChain` palauttaa [primary, fallback1, ...] -listan.
`RoutingEngine.IsToolsEnabled` kertoo aktivoiko policy agenttilooppin.
`RoutingEngine.GetQueryBackend` palauttaa `"cosmos"` tai `"mssql"` policyn `QueryBackend`-kentän perusteella.

## Query-backendit (agenttiloop)

Backend valitaan automaattisesti policyn mukaan `ChatEndpoints.MapChatEndpoints`:ssa:
- **Cosmos DB** (`QueryBackend: "cosmos"`): Cosmos DB SQL NoSQL-syntaksi, schema `c.content.paikkakunta / pvm / lampotila`
- **MS SQL** (`QueryBackend: "mssql"`): T-SQL-syntaksi, taulu `mittaukset`, sarakkeet `id / paikkakunta / pvm / lampotila`

Molemmat backendit rekisteröity keyed serviceiksi (`"cosmos"` ja `"mssql"`), jolloin molemmat ovat aktiivisena samanaikaisesti. Backendille annetaan myös eri system prompt ja tool-kuvaus, jotta LLM osaa generoida oikean SQL-syntaksin.

## Agenttiloop — SQL-generoinnin ohjaus

`MaxToolIterations = 5` — yläraja tool-kutsujen määrälle per pyyntö. Estää äärettömän loopin.

LLM generoi SQL-kyselyt itse saamansa tool-kuvauksen ja system promptin perusteella. Kummallekin backendille on omat rajoitukset jotka on kirjattu sekä system promptiin että tool-kuvaukseen:

**Cosmos DB (`CosmosSystemPrompt` + `CosmosQueryDatabaseTool`):**
- `ORDER BY` + `GROUP BY` ei toimi yhdessä — kielletty eksplisiittisesti
- Ei `MONTH()` / `YEAR()` -funktioita — käytä `STARTSWITH` tai `SUBSTRING(c.content.pvm, 0, 7)`
- Kuukausittainen ryhmittely: `GROUP BY SUBSTRING(c.content.pvm, 0, 7)`
- Min/max kuukaudesta: hae kaikki kuukaudet ilman ORDER BY, päättele itse tuloksista

**MS SQL (`SqlSystemPrompt` + `SqlQueryDatabaseTool`):**
- `LIMIT` ei ole T-SQL:ää — käytä `TOP N`
- Kielto on sekä system promptissa että tool-kuvauksessa (LLM unohtaa helposti agenttiloopin aikana)

## Resilientti kutsulogiikka (AzureOpenAIClient)

- **Retry**: `MaxRetries` uudelleenyritystä lineaarisella viiveellä (`RetryDelayMs * yritysnumero`)
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
- Kansiorakenne vastaa namespace-hierarkiaa (`LlmGateway.<Kansio>`)
- Lokitus structured logging -tyylillä (`Policy=`, `ModelKey=`, `LatencyMs=`, `Backend=` jne.)
- Kaikki koodikommentit suomeksi

## Testaus

```bash
# chat_default policy (gpt4oMini)
curl -X POST http://localhost:5079/api/chat -H "Content-Type: application/json" -H "X-Api-Key: YOUR-API-KEY" -d "{\"message\": \"Hei\"}"

# critical policy (gpt4)
curl -X POST http://localhost:5079/api/chat -H "Content-Type: application/json" -H "X-Api-Key: YOUR-API-KEY" -d "{\"message\": \"Analysoi tämä\", \"policy\": \"critical\"}"

# tools policy — Cosmos DB backend, LLM generoi NoSQL-kyselyn
curl -X POST http://localhost:5079/api/chat -H "Content-Type: application/json" -H "X-Api-Key: YOUR-API-KEY" -d "{\"message\": \"Mikä oli lämpötilan keskiarvo Helsingissä helmikuussa 2025?\", \"policy\": \"tools\"}"

# tools_sql policy — MS SQL backend, LLM generoi T-SQL-kyselyn
curl -X POST http://localhost:5079/api/chat -H "Content-Type: application/json" -H "X-Api-Key: YOUR-API-KEY" -d "{\"message\": \"Mikä oli lämpötilan keskiarvo Helsingissä helmikuussa 2025?\", \"policy\": \"tools_sql\"}"
```

OpenAPI-schema: `http://localhost:5079/openapi/v1.json`


## Flow

### Yksinkertainen kutsu (policy: "chat_default" tai "critical")
1. POST /api/chat `{ "message": "Kerro vitsi", "policy": "critical" }`
2. `ChatEndpoints` → `RoutingEngine.ResolveModelChain` → `["gpt4"]`
3. `IsToolsEnabled` → false → `HandleSimpleAsync`
4. `AzureOpenAIClient.GetChatCompletionAsync` — circuit breaker + retry
5. Vastaus: `{ "reply": "...", "model": "gpt-4", "usage": {...}, "requestId": "..." }`

### Function calling -agenttiloop, Cosmos DB (policy: "tools")
1. POST /api/chat `{ "message": "Mikä oli keskilämpötila Helsingissä helmikuussa?", "policy": "tools" }`
2. `ChatEndpoints` → `IsToolsEnabled` → true → `HandleWithToolsAsync`
3. `GetQueryBackend` → `"cosmos"` → `CosmosQueryService` + Cosmos-tool-kuvaus + Cosmos-system prompt
4. Rakennetaan messages-lista + tool-määrittelyt
5. `AzureOpenAIClient.GetRawCompletionAsync(messages, tools, "gpt4")`
6. Azure palauttaa `finish_reason: "tool_calls"` → `query_database(sql="SELECT AVG(c.content.lampotila) ...")`
7. `CosmosQueryService.ExecuteQueryAsync(sql)` → JSON-tulokset
8. Lisätään tool-tulos messages-listaan, loop uudelleen
9. Azure palauttaa `finish_reason: "stop"` → lopullinen vastaus
10. Vastaus: `{ "reply": "Helmikuussa 2025 keskilämpötila Helsingissä oli -3.2°C.", ... }`

### Function calling -agenttiloop, MS SQL (policy: "tools_sql")
1. POST /api/chat `{ "message": "Mikä oli keskilämpötila Helsingissä helmikuussa?", "policy": "tools_sql" }`
2. `ChatEndpoints` → `IsToolsEnabled` → true → `HandleWithToolsAsync`
3. `GetQueryBackend` → `"mssql"` → `SqlQueryService` + SQL-tool-kuvaus + SQL-system prompt
4. Rakennetaan messages-lista + tool-määrittelyt
5. `AzureOpenAIClient.GetRawCompletionAsync(messages, tools, "gpt4")`
6. Azure palauttaa `finish_reason: "tool_calls"` → `query_database(sql="SELECT AVG(lampotila) FROM mittaukset ...")`
7. `SqlQueryService.ExecuteQueryAsync(sql)` → JSON-tulokset (Microsoft.Data.SqlClient)
8. Lisätään tool-tulos messages-listaan, loop uudelleen
9. Azure palauttaa `finish_reason: "stop"` → lopullinen vastaus
10. Vastaus: `{ "reply": "Helmikuussa 2025 keskilämpötila Helsingissä oli -3.2°C.", ... }`

## Git
Important: I make commits manually DO NOT EVER DO COMMITS

## Facts and hallucinating
Use facts NOT hallucinating
