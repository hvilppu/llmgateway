# LlmGateway

ASP.NET Core (.NET 10) minimal API gateway Azure OpenAI -palvelulle. Tukee sekä Cosmos DB- että MS SQL -taustakantoja function calling -agenttiloopissa.

## Rakenne

```
Program.cs                              DI-rekisteröinnit, middleware-pipeline

Endpoints/
  ChatEndpoints.cs                      POST /api/chat endpoint — yksinkertainen + agenttiloop (function calling)
                                        Erillinen system prompt ja tool-kuvaus Cosmos DB- ja MS SQL -poluille

Infrastructure/
  AzureOpenAIClient.cs                  IAzureOpenAIClient + toteutus (retry, circuit breaker, GetRawCompletionAsync, GetEmbeddingAsync)
  AzureOpenAIOptions.cs                 Konfiguraatio-optiot
  CircuitBreaker.cs                     ICircuitBreaker, InMemoryCircuitBreaker, CircuitBreakerOptions

Middleware/
  ApiKeyMiddleware.cs                   X-Api-Key -headerin tarkistus, ApiKeyOptions

Models/
  Models.cs                             ChatRequest, ChatResponse, UsageInfo, Azure-vastausmallit + tool calling -mallit

Routing/
  Routing.cs                            IRoutingEngine, RoutingEngine, PolicyOptions, PolicyConfig
                                        PolicyConfig: PrimaryModel, Fallbacks, ToolsEnabled, RagEnabled, QueryBackend
                                        IRoutingEngine: ResolveModelChain, IsToolsEnabled, GetQueryBackend, IsRagEnabled

Services/
  QueryService.cs                       IQueryService
                                        CosmosQueryService — Cosmos DB NoSQL SELECT -kyselyt
                                        SqlQueryService    — MS SQL / Azure SQL T-SQL SELECT -kyselyt
                                        CosmosOptions      — Cosmos DB -yhteysasetukset
                                        SqlOptions         — MS SQL -yhteysasetukset
  SchemaService.cs                      ISchemaProvider
                                        CosmosSchemaProvider — skeema näytedokumentista (TOP 1), 60 min välimuisti
                                        SqlSchemaProvider    — skeema INFORMATION_SCHEMA.COLUMNS, 60 min välimuisti
  RagService.cs                         IRagService, CosmosRagService, RagOptions
                                        VectorDistance-haku kuukausiraportit-containerista, TopK=3

SyncFunction/                           Erillinen Azure Functions -sovellus
  Program.cs                            DI-rekisteröinnit, hosted services
  CosmosToSqlTrigger.cs                 Timer trigger (15 min) — kutsuu CosmosSyncService + MonthlyReportService
  Services/
    CosmosSyncService.cs                Cosmos DB → MS SQL synkronointi (_ts-vesimerkki, MERGE INTO)
    MigrationService.cs                 IHostedService — SQL-taulujen luonti käynnistyksessä (IF NOT EXISTS)
    MonthlyReportService.cs             Kuukausiraporttien generointi: GPT-4o-mini (kuvaus) + embedding → upsert kuukausiraportit
                                        ReportBackfillService — IHostedService, ajaa GenerateAllMonthsReportsAsync käynnistyksessä (historiadatan backfill)
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
| `SyncFunction/` | `SyncFunction` |
| `SyncFunction/Services/` | `SyncFunction.Services` |

## Teknologiat

- .NET 10 minimal API
- Sisäänrakennettu OpenAPI (`AddOpenApi` / `MapOpenApi`) — ei Swashbuckle
- `IOptions<T>` konfiguraatiolle
- `IHttpClientFactory` / typed client HttpClient-hallintaan
- Circuit breaker in-memory toteutuksella
- Policy-pohjainen model routing
- `Microsoft.Data.SqlClient` MS SQL -kyselyihin
- Keyed DI (`AddKeyedSingleton`) — molemmat query- ja schema-backendit aktiivisena samanaikaisesti
- Dynaaminen skeemahaku välimuistilla (`ISchemaProvider`) — system prompt rakennetaan ajonaikana

## Konfiguraatio (appsettings)

```json
"ApiKey": {
  "Key": "YOUR-GATEWAY-API-KEY"
},
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
  },
  "EmbeddingDeployment": "YOUR-EMBEDDING-DEPLOYMENT-NAME"
},
"Policies": {
  "chat_default": { "PrimaryModel": "gpt4oMini" },
  "critical":     { "PrimaryModel": "gpt4" },
  "tools":        { "PrimaryModel": "gpt4", "ToolsEnabled": true, "QueryBackend": "cosmos" },
  "tools_sql":    { "PrimaryModel": "gpt4", "ToolsEnabled": true, "QueryBackend": "mssql" },
  "rag":          { "PrimaryModel": "gpt4", "RagEnabled": true, "QueryBackend": "cosmos" }
},
"Rag": {
  "DatabaseName": "ragdb",
  "RaportitContainerName": "kuukausiraportit",
  "TopK": 3
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
- **Cosmos DB** (`QueryBackend: "cosmos"`): Cosmos DB SQL NoSQL-syntaksi
- **MS SQL** (`QueryBackend: "mssql"`): T-SQL-syntaksi

Molemmat backendit rekisteröity keyed serviceiksi (`"cosmos"` ja `"mssql"`), jolloin molemmat ovat aktiivisena samanaikaisesti. Backendille annetaan eri system prompt, tool-kuvaus ja skeema, jotta LLM osaa generoida oikean SQL-syntaksin.

## Dynaaminen skeemahaku

Skeema haetaan tietokannasta ajonaikana ja injektoidaan system promptiin ennen LLM-kutsua:
- **Cosmos DB**: `SELECT TOP 1 * FROM c` → JSON-rakenne puretaan rekursiivisesti pistenotaatiolla (`c.content.paikkakunta` jne.)
- **MS SQL**: `INFORMATION_SCHEMA.COLUMNS` → taulut ja sarakkeet tyyppitinetoineen

Molemmat käyttävät `SemaphoreSlim` double-check -välimuistia (60 min). Jos haku epäonnistuu, system prompt rakentuu ilman skeemaa — pyyntö ei kaadu.

Keyed DI: `ISchemaProvider` rekisteröity avaimilla `"cosmos"` ja `"mssql"` samoin kuin `IQueryService`.

## Agenttiloop — SQL-generoinnin ohjaus

`MaxToolIterations = 5` — yläraja tool-kutsujen määrälle per pyyntö. Estää äärettömän loopin.

LLM generoi SQL-kyselyt itse saamansa tool-kuvauksen ja system promptin perusteella. Kummallekin backendille on omat rajoitukset jotka on kirjattu sekä system promptiin että tool-kuvaukseen:

**Cosmos DB (`BuildCosmosSystemPrompt` + `CosmosQueryDatabaseTool`):**
- `ORDER BY` + `GROUP BY` ei toimi yhdessä — kielletty eksplisiittisesti
- Ei `MONTH()` / `YEAR()` -funktioita — käytä `STARTSWITH` tai `SUBSTRING(c.content.pvm, 0, 7)`
- Kuukausittainen ryhmittely: `GROUP BY SUBSTRING(c.content.pvm, 0, 7)`
- Min/max kuukaudesta: hae kaikki kuukaudet ilman ORDER BY, päättele itse tuloksista

**MS SQL (`BuildSqlSystemPrompt` + `SqlQueryDatabaseTool`):**
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

# rag policy — embedding-haku kuukausiraporteista + Cosmos DB -agenttiloop
curl -X POST http://localhost:5079/api/chat -H "Content-Type: application/json" -H "X-Api-Key: YOUR-API-KEY" -d "{\"message\": \"Millainen talvi 2024 oli Helsingissä?\", \"policy\": \"rag\"}"
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
3. `GetQueryBackend` → `"cosmos"` → `CosmosQueryService` + `CosmosSchemaProvider`
4. `CosmosSchemaProvider.GetSchemaAsync()` → skeema välimuistista tai `SELECT TOP 1 * FROM c`
5. `BuildCosmosSystemPrompt(schema)` → system prompt skeemalla injektoituna
6. Rakennetaan messages-lista + tool-määrittelyt
7. `AzureOpenAIClient.GetRawCompletionAsync(messages, tools, "gpt4")`
8. Azure palauttaa `finish_reason: "tool_calls"` → `query_database(sql="SELECT AVG(c.content.lampotila) ...")`
9. `CosmosQueryService.ExecuteQueryAsync(sql)` → JSON-tulokset
10. Lisätään tool-tulos messages-listaan, loop uudelleen
11. Azure palauttaa `finish_reason: "stop"` → lopullinen vastaus
12. Vastaus: `{ "reply": "Helmikuussa 2025 keskilämpötila Helsingissä oli -3.2°C.", ... }`

### Function calling -agenttiloop, MS SQL (policy: "tools_sql")
1. POST /api/chat `{ "message": "Mikä oli keskilämpötila Helsingissä helmikuussa?", "policy": "tools_sql" }`
2. `ChatEndpoints` → `IsToolsEnabled` → true → `HandleWithToolsAsync`
3. `GetQueryBackend` → `"mssql"` → `SqlQueryService` + `SqlSchemaProvider`
4. `SqlSchemaProvider.GetSchemaAsync()` → skeema välimuistista tai `INFORMATION_SCHEMA.COLUMNS`
5. `BuildSqlSystemPrompt(schema)` → system prompt skeemalla injektoituna
6. Rakennetaan messages-lista + tool-määrittelyt
7. `AzureOpenAIClient.GetRawCompletionAsync(messages, tools, "gpt4")`
8. Azure palauttaa `finish_reason: "tool_calls"` → `query_database(sql="SELECT AVG(lampotila) FROM mittaukset ...")`
9. `SqlQueryService.ExecuteQueryAsync(sql)` → JSON-tulokset (Microsoft.Data.SqlClient)
10. Lisätään tool-tulos messages-listaan, loop uudelleen
11. Azure palauttaa `finish_reason: "stop"` → lopullinen vastaus
12. Vastaus: `{ "reply": "Helmikuussa 2025 keskilämpötila Helsingissä oli -3.2°C.", ... }`

### RAG-polku (policy: "rag")
1. POST /api/chat `{ "message": "Millainen talvi 2024 oli Helsingissä?", "policy": "rag" }`
2. `ChatEndpoints` → `RoutingEngine.ResolveModelChain` → `["gpt4"]`
3. `IsRagEnabled` → true → RAG-haara (ennen `IsToolsEnabled`-tarkistusta)
4. `CosmosSchemaProvider.GetSchemaAsync()` → skeema välimuistista tai `SELECT TOP 1 * FROM c`
5. `AzureOpenAIClient.GetEmbeddingAsync(request.Message)` → `float32[1536]`
6. `CosmosRagService.GetContextAsync(queryEmbedding)` → VectorDistance-haku `kuukausiraportit`-containerista → top-3 kuvaukset merkkijonona
7. `BuildRagSystemPrompt(ragContext, schema)` → system prompt RAG-kontekstilla ja skeemalla injektoituna
8. `HandleWithToolsAsync(... CosmosTools ...)` → agenttiloop Cosmos-työkaluilla
9. `AzureOpenAIClient.GetRawCompletionAsync(messages, CosmosTools, "gpt4")`
10a. Azure palauttaa `finish_reason: "stop"` → LLM vastaa RAG-kontekstin pohjalta, ei tarvita DB-kyselyä
10b. Azure palauttaa `finish_reason: "tool_calls"` → `query_database(sql="SELECT AVG(...)")` → tarkka luku DB:stä → loop jatkuu
11. Azure palauttaa `finish_reason: "stop"` → lopullinen vastaus
12. Vastaus: `{ "reply": "Talvi 2024 Helsingissä oli ankea ja kylmä. Tammikuun keskilämpötila oli -6.1°C...", ... }`

**Huom:** RAG-polku käyttää aina `CosmosTools` + `CosmosQueryService` riippumatta `QueryBackend`-arvosta.
Tarkat luvut haetaan AINA `query_database`-työkalulla — RAG-konteksti on vain sanallista taustaa.

## Git
Tärkeää: Teen commitit itse — ÄLÄ KOSKAAN tee committeja automaattisesti

## Faktat ja hallusinointi
Käytä faktoja — älä hallusinoi

## Dokumentaatiotiedostot

| Tiedosto | Sisältö — milloin luet / päivität |
|----------|-----------------------------------|
| `CLAUDE.md` | Arkkitehtuuri, rakenne, konfiguraatio, flow — projektin pääohje Claudelle |
| `TERMS.md` | Termit ja käsitteet — päivitä kun lisäät uuden konseptin tai komponentin |
| `INFRA.md` | Azure-infran käyttöönotto-ohjeet — päivitä kun Bicep tai workflow muuttuu |
| `INTRO.md` | Yleiskuvaus projektista — harvoin muuttuu |
| `ERRORS.md` | Tunnetut virheet ja niiden ratkaisut |
| `PROBLEMS.md` | Avoimet ongelmat ja rajoitukset |
| `RAG.md` | RAG-arkkitehtuurin kuvaus ja ohjeet |
| `FAQ.md` | Usein kysytyt kysymykset |
| `architecture.mmd` | Mermaid-kaavio — päivitä kun arkkitehtuuri muuttuu (tekstipohjainen, helppo) |
| `architecture.drawio` | Visuaalinen draw.io-kaavio — päivitetään harkiten, ei jokaisen muutoksen yhteydessä |

## Dokumentaation synkronointi

Kun teet koodimuutoksia, päivitä aina myös alla listatut dokumentit. Tarkista taulukko ennen kuin ilmoitat tehtävän valmiiksi.

| Muutos koodissa | Päivitä nämä tiedostot |
|-----------------|------------------------|
| Uusi policy tai olemassaolevan muutos | `CLAUDE.md` (Policyt-osio), `TERMS.md` (Policy-rivi), `architecture.mmd` (reitityssolmu) |
| Uusi backend tai query-polku | `CLAUDE.md` (Query-backendit), `TERMS.md`, `architecture.mmd` |
| Uusi service tai rajapinta (`IFoo`, `FooService`) | `CLAUDE.md` (Rakenne + Flow), `TERMS.md` jos uusi käsite, `architecture.mmd` |
| Uusi endpoint | `CLAUDE.md` (Rakenne), `architecture.mmd` |
| Muutos resilienssilogiikkaan (retry, CB, timeout) | `CLAUDE.md`, `TERMS.md` (Resilienssiimallit-osio) |
| Muutos Bicep / infra-tiedostoihin | `INFRA.md` |
| Muutos appsettings-rakenteeseen | `CLAUDE.md` (Konfiguraatio-osio) |
| Uusi termi tai konsepti jota ei ole selitetty | `TERMS.md` |

**`architecture.drawio`** — päivitetään harkiten erikseen, ei jokaisen muutoksen yhteydessä. Se on visuaalinen snapshot, ei living document.
