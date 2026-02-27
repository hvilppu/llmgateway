# LlmGateway – Architecture Diagram Prompt

Use the description below to generate an architecture diagram. Draw it as a clean, top-down flow diagram with clearly labeled boxes and arrows.

---

## Prompt for ChatGPT

Draw a software architecture diagram for an ASP.NET Core (.NET 10) API gateway called **LlmGateway**. Use a top-down flow layout with labeled boxes and directional arrows. Include the following components and relationships:

---

### Components

**Client**
- External HTTP client (e.g. curl, web app)

**ASP.NET Core Middleware Pipeline** (inside the gateway process)
- HTTPS Redirection middleware

**ChatEndpoints** (`POST /api/chat`)
- Receives `ChatRequest` (fields: `message`, `policy`, `conversationId`)
- Generates `requestId` from `HttpContext.TraceIdentifier`
- Calls RoutingEngine to resolve model chain and check `IsToolsEnabled`
- **Simple path** (ToolsEnabled=false): calls `GetChatCompletionAsync`, iterates fallback chain on error
- **Agent loop path** (ToolsEnabled=true): calls `GetRawCompletionAsync` with tool definitions in a loop; on `finish_reason=tool_calls` executes tools (search_documents → RagService, query_database → QueryService); repeats until `finish_reason=stop` or max iterations
- Returns `ChatResponse` (fields: `reply`, `model`, `usage`, `requestId`)
- Error handling: `CircuitBreakerOpenException` → 503, `HttpRequestException` → 502, `Exception` → 500

**RoutingEngine** (singleton)
- Reads `request.Policy` (default: `"chat_default"`)
- `ResolveModelChain(request)` → returns ordered list `[primary, fallback1, ...]`
- `IsToolsEnabled(request)` → bool (true if policy has `ToolsEnabled: true`)
- Falls back to `chat_default` if policy unknown

**AzureOpenAIClient** (typed HttpClient)
- `GetChatCompletionAsync`: simple single-shot chat call (retry + circuit breaker)
- `GetRawCompletionAsync`: chat call with tool definitions, returns raw Azure response including `finish_reason` and `tool_calls`
- `GetEmbeddingAsync`: POST to Embeddings API for RAG vector generation
- Per-request timeout via `CancellationTokenSource.CancelAfter`
- Retry loop up to `MaxRetries` with exponential delay
- Calls `ICircuitBreaker.RecordFailure/RecordSuccess`

**InMemoryCircuitBreaker** (singleton)
- Per-model state in `ConcurrentDictionary`
- States: Closed → Open → Half-Open → Closed
- Opens after `FailureThreshold` consecutive failures
- Stays open for `BreakDurationSeconds`, then enters Half-Open

**CosmosRagService** (singleton)
- `RetrieveContextAsync(query)`: tool handler for `search_documents`
- Calls `GetEmbeddingAsync(query)` to get query vector
- Runs `VectorDistance` query on Cosmos DB container
- Returns top-K document content strings concatenated

**CosmosQueryService** (singleton)
- `ExecuteQueryAsync(sql)`: tool handler for `query_database`
- Validates SQL starts with SELECT (rejects DELETE/UPDATE/DROP)
- Executes Cosmos DB SQL query
- Returns results as JSON array string

**Azure OpenAI Chat API** (external)
- `POST /openai/deployments/{deploymentName}/chat/completions`
- Deployments: `gpt4`, `gpt4oMini`
- Supports `tools` parameter for function calling

**Azure OpenAI Embeddings API** (external)
- `POST /openai/deployments/{embeddingDeployment}/embeddings`
- Deployment: `text-embedding-3-small`

**Cosmos DB NoSQL** (external)
- Container with documents: `{ id, content: { paikkakunta, pvm, lämpötila }, embedding: float[] }`
- Vector index on `embedding` field for `VectorDistance` queries

**appsettings.json** (configuration)
- `AzureOpenAI`: Endpoint, ApiKey, ApiVersion, TimeoutMs, MaxRetries, RetryDelayMs, EmbeddingDeployment, Deployments
- `Policies`: named policies with PrimaryModel, Fallbacks, ToolsEnabled
- `CosmosRag`: ConnectionString, DatabaseName, ContainerName, TopK, VectorField, ContentField
- `CircuitBreaker`: FailureThreshold, BreakDurationSeconds

**OpenAPI** (`GET /openapi/v1.json`)
- Built-in .NET 10 OpenAPI endpoint, available in Development

---

### Key Relationships / Arrows

**Simple path (ToolsEnabled=false):**
1. Client → HTTPS Redirection → ChatEndpoints: `POST /api/chat`
2. ChatEndpoints → RoutingEngine: `ResolveModelChain(request)` → `[modelKey, ...]`
3. ChatEndpoints → AzureOpenAIClient: `GetChatCompletionAsync(request, requestId, modelKey)`
4. AzureOpenAIClient → InMemoryCircuitBreaker: `IsOpen(modelKey)` check
5. AzureOpenAIClient → Azure OpenAI Chat API: HTTP POST with JSON payload
6. Azure OpenAI Chat API → AzureOpenAIClient: HTTP response
7. AzureOpenAIClient → InMemoryCircuitBreaker: `RecordSuccess` or `RecordFailure`
8. AzureOpenAIClient → AzureOpenAIClient: retry loop (dashed self-arrow)
9. ChatEndpoints → ChatEndpoints: try next model in chain on failure (fallback loop)
10. ChatEndpoints → Client: `ChatResponse` (200 / 503 / 502 / 500)

**Agent loop path (ToolsEnabled=true):**
1–4. Same as above
5. ChatEndpoints → AzureOpenAIClient: `GetRawCompletionAsync(messages, tools, modelKey)`
6. AzureOpenAIClient → Azure OpenAI Chat API: HTTP POST with `tools` array
7. Azure OpenAI Chat API → AzureOpenAIClient: `finish_reason=tool_calls` + tool_calls list
8a. ChatEndpoints → CosmosRagService: `search_documents` tool → `RetrieveContextAsync(query)`
8b. CosmosRagService → AzureOpenAIClient: `GetEmbeddingAsync(query)`
8c. AzureOpenAIClient → Azure OpenAI Embeddings API: HTTP POST
8d. CosmosRagService → Cosmos DB: VectorDistance query → top-K docs
   OR
8e. ChatEndpoints → CosmosQueryService: `query_database` tool → `ExecuteQueryAsync(sql)`
8f. CosmosQueryService → Cosmos DB: SQL SELECT query → JSON results
9. ChatEndpoints → AzureOpenAIClient: `GetRawCompletionAsync` again with tool results
10. Azure OpenAI Chat API → AzureOpenAIClient: `finish_reason=stop` + final answer
11. ChatEndpoints → Client: `ChatResponse`

**Configuration (dashed yellow arrows):**
- RoutingEngine → appsettings: reads `Policies` + `Deployments`
- AzureOpenAIClient → appsettings: reads `AzureOpenAI` options
- InMemoryCircuitBreaker → appsettings: reads `CircuitBreaker` options
- CosmosRagService → appsettings: reads `CosmosRag` options
- CosmosQueryService → appsettings: reads `CosmosRag` options

---

### Visual Style Suggestions

- Use a **light blue** background for gateway-internal components
- Use **gray** for external systems (Client, Azure OpenAI APIs, Cosmos DB, appsettings)
- Use **red/orange** to highlight the Circuit Breaker
- Use **green** for tool execution components (CosmosRagService, CosmosQueryService)
- Use **dashed arrows** for error paths, retry loops, and config reads
- Use **solid arrows** for the happy path
- Show the agent loop as a box or group with a loop-back arrow
- Add a legend for arrow types (happy path, error/retry, tool call, config read)

---

### HTTP Response Table

| Situation                  | HTTP Status               |
|----------------------------|---------------------------|
| Success                    | 200 OK                    |
| Circuit breaker open       | 503 Service Unavailable   |
| Azure error                | 502 Bad Gateway           |
| Agent loop max iterations  | 500 Internal Server Error |
| Unexpected error           | 500 Internal Server Error |

---

### Policy Routing Examples

| `request.policy` | PrimaryModel | ToolsEnabled | Behaviour              |
|------------------|--------------|--------------|------------------------|
| `null` / missing | `gpt4oMini`  | false        | Simple chat call       |
| `"critical"`     | `gpt4`       | false        | Simple + fallback chain|
| `"tools"`        | `gpt4`       | true         | Function calling loop  |
| unknown value    | `gpt4oMini`  | false        | Fallback to chat_default|

---

### Tool Definitions (function calling)

| Tool name          | When LLM uses it                        | Handler            |
|--------------------|-----------------------------------------|--------------------|
| `search_documents` | Explanatory / semantic questions        | CosmosRagService   |
| `query_database`   | Aggregation queries (AVG, SUM, COUNT)   | CosmosQueryService |
