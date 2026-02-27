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
- Calls RoutingEngine to resolve model
- Calls AzureOpenAIClient to get completion
- Returns `ChatResponse` (fields: `reply`, `model`, `usage`, `requestId`)
- Error handling: catches `CircuitBreakerOpenException` → 503, `HttpRequestException` → 502, `Exception` → 500

**RoutingEngine** (singleton)
- Reads `request.Policy` (default: `"chat_default"`)
- Looks up policy in `PolicyOptions` (from appsettings `"Policies"`)
- Returns `modelKey` string (e.g. `"gpt4oMini"` or `"gpt4"`)
- Validates that `modelKey` exists in `AzureOpenAIOptions.Deployments`

**AzureOpenAIClient** (typed HttpClient)
- Checks `ICircuitBreaker.IsOpen(modelKey)` — throws `CircuitBreakerOpenException` if open
- Resolves `deploymentName` from `AzureOpenAIOptions.Deployments[modelKey]`
- Sends HTTP POST to Azure OpenAI REST API
- Per-request timeout via `CancellationTokenSource.CancelAfter`
- Retry loop: up to `MaxRetries` attempts with exponential delay
- Transient errors: HTTP 408, 429, 5xx and `TaskCanceledException`
- Calls `ICircuitBreaker.RecordFailure(modelKey)` on error
- Calls `ICircuitBreaker.RecordSuccess(modelKey)` on success

**InMemoryCircuitBreaker** (singleton)
- Per-model state stored in `ConcurrentDictionary`
- States: Closed → Open → Half-Open → Closed
- Opens after `FailureThreshold` consecutive failures
- Stays open for `BreakDurationSeconds`, then enters Half-Open

**Azure OpenAI REST API** (external)
- Endpoint: `POST /openai/deployments/{deploymentName}/chat/completions?api-version=...`
- Two deployments: `gpt4` and `gpt4oMini`

**appsettings.json** (configuration)
- `AzureOpenAI`: Endpoint, ApiKey, ApiVersion, TimeoutMs, MaxRetries, RetryDelayMs, Deployments
- `Policies`: named policies mapping to `PrimaryModel`
- `CircuitBreaker`: FailureThreshold, BreakDurationSeconds

**OpenAPI** (`GET /openapi/v1.json`)
- Built-in .NET 10 OpenAPI endpoint, available in Development

---

### Key Relationships / Arrows

1. **Client → HTTPS Redirection → ChatEndpoints**: inbound HTTP POST `/api/chat`
2. **ChatEndpoints → RoutingEngine**: `ResolveModelKey(request)` → returns `modelKey`
3. **ChatEndpoints → AzureOpenAIClient**: `GetChatCompletionAsync(request, requestId, modelKey)`
4. **AzureOpenAIClient → InMemoryCircuitBreaker**: `IsOpen(modelKey)` check before sending
5. **AzureOpenAIClient → Azure OpenAI REST API**: HTTP POST with JSON payload
6. **Azure OpenAI REST API → AzureOpenAIClient**: HTTP response (200 or error)
7. **AzureOpenAIClient → InMemoryCircuitBreaker**: `RecordSuccess` or `RecordFailure`
8. **AzureOpenAIClient → AzureOpenAIClient**: retry loop (dashed self-arrow with label "retry up to MaxRetries")
9. **RoutingEngine → appsettings**: reads `Policies` and `Deployments`
10. **AzureOpenAIClient → appsettings**: reads `AzureOpenAI` options
11. **InMemoryCircuitBreaker → appsettings**: reads `CircuitBreaker` options
12. **ChatEndpoints → Client**: returns `ChatResponse` (200 / 503 / 502 / 500)

---

### Visual Style Suggestions

- Use a **light blue** background for gateway-internal components
- Use **gray** for external systems (Client, Azure OpenAI API, appsettings)
- Use **red/orange** to highlight the Circuit Breaker
- Use **dashed arrows** for error paths and retry loops
- Use **solid arrows** for the happy path
- Add a legend for arrow types (happy path, error path, retry, config read)

---

### HTTP Response Table (include as a small table in the diagram or as a sidebar)

| Situation                  | HTTP Status             |
|---------------------------|-------------------------|
| Success                   | 200 OK                  |
| Circuit breaker open      | 503 Service Unavailable |
| Azure error               | 502 Bad Gateway         |
| Unexpected error          | 500 Internal Server Error |

---

### Policy Routing Examples (include as a note or legend)

| `request.policy`   | Resolved policy    | modelKey     | Azure deployment |
|--------------------|--------------------|--------------|-----------------|
| `null` / missing   | `chat_default`     | `gpt4oMini`  | mini-deployment |
| `"critical"`       | `critical`         | `gpt4`       | gpt4-deployment |
| unknown value      | fallback → `chat_default` | `gpt4oMini` | mini-deployment |
