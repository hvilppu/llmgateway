using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace LlmGateway;

public static class ChatEndpoints
{
    private const int MaxToolIterations = 5;

    // Yksinkertaisen polun system prompt: rajoittaa aiheeseen ilman työkaluja.
    private const string SimpleSystemPrompt =
        "Olet assistentti joka vastaa AINOASTAAN Suomen kaupunkien lämpötila- ja säädataan liittyviin kysymyksiin. " +
        "Vastaa suomeksi. Jos kysymys ei liity aiheeseen, kieltäydy kohteliaasti.";

    // Cosmos DB -polun system prompt.
    private const string CosmosSystemPrompt =
        "Olet assistentti joka vastaa AINOASTAAN Suomen kaupunkien lämpötila- ja säädataan liittyviin kysymyksiin. " +
        "Vastaa suomeksi. Jos kysymys ei liity lämpötila- tai säädataan, kieltäydy kohteliaasti. " +
        "Sinulla on käytettävissä työkalu:\n" +
        "- query_database: suorita Cosmos DB SQL -kysely (käytä aggregointiin: keskiarvot, summat, määrät, suodatus)\n" +
        "TÄRKEÄ RAJOITUS: Cosmos DB SQL ei tue ORDER BY:tä GROUP BY:n kanssa. " +
        "Kun haet kylmintä tai lämpimintä kuukautta: hae KAIKKI kuukaudet GROUP BY:llä (ilman ORDER BY), " +
        "katso palautetut kuukausikeskiarvot ja päättele itse mikä on pienin tai suurin. " +
        "Käytä työkalua aina kun kysymys koskee dataa.";

    // MS SQL -polun system prompt.
    private const string SqlSystemPrompt =
        "Olet assistentti joka vastaa AINOASTAAN Suomen kaupunkien lämpötila- ja säädataan liittyviin kysymyksiin. " +
        "Vastaa suomeksi. Jos kysymys ei liity lämpötila- tai säädataan, kieltäydy kohteliaasti. " +
        "Sinulla on käytettävissä työkalu:\n" +
        "- query_database: suorita T-SQL SELECT -kysely MS SQL -tietokantaan (käytä aggregointiin: keskiarvot, summat, määrät, suodatus)\n" +
        "TÄRKEÄ T-SQL-rajoitus: älä KOSKAAN käytä LIMIT — se ei toimi SQL Serverissä. " +
        "Rivien rajaamiseen käytä TOP N (esim. SELECT TOP 1 ...). " +
        "Käytä työkalua aina kun kysymys koskee dataa.";

    // Cosmos DB -tool-kuvaus (NoSQL-syntaksi).
    private static readonly object CosmosQueryDatabaseTool = new
    {
        type = "function",
        function = new
        {
            name = "query_database",
            description = "Suorittaa Cosmos DB SQL SELECT -kyselyn tietokannalle. " +
                          "Käytä aggregointiin (AVG, SUM, COUNT, MIN, MAX) ja suodatukseen. " +
                          "Tietokannan schema: c.id (string), c.content.paikkakunta (string), " +
                          "c.content.pvm (date string, esim. '2025-01-15'), c.content.lampotila (number). " +
                          "Vain SELECT-kyselyt sallittu. " +
                          "RAJOITUS: ORDER BY ei toimi GROUP BY:n kanssa — älä yhdistä niitä. " +
                          "Kuukausittainen ryhmittely: GROUP BY SUBSTRING(c.content.pvm,0,7). " +
                          "Ei MONTH()- tai YEAR()-funktiota — käytä STARTSWITH tai SUBSTRING päivämäärille.",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    sql = new
                    {
                        type = "string",
                        description = "Cosmos DB SQL SELECT -kysely. Esimerkkejä: " +
                                      "1) Kuukausikeskiarvot (kylmin/lämpimin kuukausi): " +
                                      "SELECT SUBSTRING(c.content.pvm,0,7) AS kk, AVG(c.content.lampotila) AS avg " +
                                      "FROM c WHERE c.content.paikkakunta='Tampere' AND STARTSWITH(c.content.pvm,'2024') " +
                                      "GROUP BY SUBSTRING(c.content.pvm,0,7) -- EI ORDER BY, päättele min/max itse tuloksista! " +
                                      "2) Yksittäinen kuukausi: SELECT AVG(c.content.lampotila) AS avg FROM c " +
                                      "WHERE c.content.paikkakunta='Helsinki' AND STARTSWITH(c.content.pvm,'2025-02')"
                    }
                },
                required = new[] { "sql" }
            }
        }
    };

    // MS SQL -tool-kuvaus (T-SQL-syntaksi).
    private static readonly object SqlQueryDatabaseTool = new
    {
        type = "function",
        function = new
        {
            name = "query_database",
            description = "Suorittaa T-SQL SELECT -kyselyn MS SQL -tietokannalle. " +
                          "Käytä aggregointiin (AVG, SUM, COUNT, MIN, MAX) ja suodatukseen. " +
                          "Taulu: mittaukset. Sarakkeet: id (NVARCHAR), paikkakunta (NVARCHAR), " +
                          "pvm (DATE), lampotila (FLOAT). " +
                          "Vain SELECT-kyselyt sallittu. " +
                          "KIELLETTY: LIMIT — käytä aina TOP N rivien rajaamiseen.",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    sql = new
                    {
                        type = "string",
                        description = "T-SQL SELECT -kysely. Ei LIMIT — käytä TOP N. Esimerkkejä: " +
                                      "1) Kuukauden keskiarvo: SELECT AVG(lampotila) AS avg FROM mittaukset " +
                                      "WHERE paikkakunta = 'Helsinki' AND YEAR(pvm) = 2025 AND MONTH(pvm) = 2. " +
                                      "2) Kylmin kuukausi: SELECT TOP 1 MONTH(pvm) AS kk, AVG(lampotila) AS avg " +
                                      "FROM mittaukset WHERE paikkakunta = 'Tampere' AND YEAR(pvm) = 2024 " +
                                      "GROUP BY MONTH(pvm) ORDER BY AVG(lampotila) ASC"
                    }
                },
                required = new[] { "sql" }
            }
        }
    };

    private static readonly object[] CosmosTools = [CosmosQueryDatabaseTool];
    private static readonly object[] SqlTools = [SqlQueryDatabaseTool];

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static void MapChatEndpoints(this WebApplication app)
    {
        app.MapPost("/api/chat", async (
            ChatRequest request,
            IAzureOpenAIClient client,
            IRoutingEngine routingEngine,
            [FromKeyedServices("cosmos")] IQueryService cosmosQueryService,
            [FromKeyedServices("mssql")]  IQueryService sqlQueryService,
            ILoggerFactory loggerFactory,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var logger = loggerFactory.CreateLogger("LlmGateway.ChatEndpoint");
            var requestId = httpContext.TraceIdentifier;

            using var scope = logger.BeginScope(new Dictionary<string, object>
            {
                ["requestId"] = requestId
            });

            logger.LogInformation("Received chat request. Policy={Policy}, MessageLength={MessageLength}",
                request.Policy ?? "chat_default", request.Message.Length);

            // Viesti-pituuden rajoitus — estää token-räjähdyksen ja turhan Azure-kutsun
            if (string.IsNullOrWhiteSpace(request.Message))
                return Results.Problem(
                    title: "Invalid request",
                    detail: "Message cannot be empty",
                    statusCode: StatusCodes.Status400BadRequest);

            if (request.Message.Length > 4_000)
                return Results.Problem(
                    title: "Invalid request",
                    detail: $"Message too long: {request.Message.Length} characters (max 4000)",
                    statusCode: StatusCodes.Status400BadRequest);

            try
            {
                var modelChain = routingEngine.ResolveModelChain(request);

                if (routingEngine.IsToolsEnabled(request))
                {
                    // Valitse oikea backend ja tool-kuvaukset policyn perusteella
                    var backend = routingEngine.GetQueryBackend(request);
                    var queryService = backend == "mssql" ? sqlQueryService : cosmosQueryService;
                    var tools       = backend == "mssql" ? SqlTools : CosmosTools;
                    var systemPrompt = backend == "mssql" ? SqlSystemPrompt : CosmosSystemPrompt;

                    logger.LogInformation("Tools policy resolved. Backend={Backend}", backend);

                    return await HandleWithToolsAsync(
                        request, requestId, modelChain, client, queryService, tools, systemPrompt, logger, cancellationToken);
                }
                else
                {
                    return await HandleSimpleAsync(
                        request, requestId, modelChain, client, logger, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error handling chat request");
                return Results.Problem(
                    title: "LLM gateway error",
                    detail: "Unexpected error occurred",
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        })
        .WithName("ChatCompletion");
    }

    // Yksinkertainen kutsu ilman työkaluja (vanha käyttäytyminen, takaisinyhteensopiva).
    private static async Task<IResult> HandleSimpleAsync(
        ChatRequest request,
        string requestId,
        IReadOnlyList<string> modelChain,
        IAzureOpenAIClient client,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ChatResponse? response = null;
        Exception? lastException = null;

        foreach (var modelKey in modelChain)
        {
            try
            {
                response = await client.GetChatCompletionAsync(request, requestId, modelKey, SimpleSystemPrompt, cancellationToken);

                if (modelKey != modelChain[0])
                    logger.LogInformation("Fallback model succeeded. FallbackModel={ModelKey}", modelKey);
                else
                    logger.LogInformation("Chat request handled successfully. ModelKey={ModelKey}, TotalTokens={TotalTokens}",
                        modelKey, response.Usage?.TotalTokens ?? 0);

                break;
            }
            catch (CircuitBreakerOpenException ex)
            {
                lastException = ex;
                logger.LogWarning(ex, "Circuit breaker open for {ModelKey}, trying next in chain", ex.ModelKey);
            }
            catch (HttpRequestException ex)
            {
                lastException = ex;
                logger.LogWarning(ex, "Model {ModelKey} failed, trying next in chain", modelKey);
            }
        }

        if (response is not null)
            return Results.Ok(response);

        if (lastException is CircuitBreakerOpenException cbEx)
        {
            logger.LogWarning(cbEx, "All models exhausted via circuit breaker");
            return Results.Problem(
                title: "LLM model temporarily unavailable",
                detail: "Circuit breaker is open for all configured models",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        logger.LogError(lastException, "All models in chain exhausted via provider errors");
        return Results.Problem(
            title: "LLM provider error",
            detail: "LLM service is temporarily unavailable",
            statusCode: StatusCodes.Status502BadGateway);
    }

    // Function calling -agenttiloop: LLM valitsee itse työkalut (search_documents / query_database).
    private static async Task<IResult> HandleWithToolsAsync(
        ChatRequest request,
        string requestId,
        IReadOnlyList<string> modelChain,
        IAzureOpenAIClient client,
        IQueryService queryService,
        object[] tools,
        string systemPrompt,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;

        foreach (var modelKey in modelChain)
        {
            try
            {
                var result = await RunAgentLoopAsync(
                    request, requestId, modelKey, client, queryService, tools, systemPrompt, logger, cancellationToken);
                return result;
            }
            catch (CircuitBreakerOpenException ex)
            {
                lastException = ex;
                logger.LogWarning(ex, "Circuit breaker open for {ModelKey}, trying next in chain", ex.ModelKey);
            }
            catch (HttpRequestException ex)
            {
                lastException = ex;
                logger.LogWarning(ex, "Model {ModelKey} failed, trying next in chain", modelKey);
            }
        }

        if (lastException is CircuitBreakerOpenException cbEx2)
        {
            logger.LogWarning(cbEx2, "All models exhausted via circuit breaker");
            return Results.Problem(
                title: "LLM model temporarily unavailable",
                detail: "Circuit breaker is open for all configured models",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        logger.LogError(lastException, "All models in chain exhausted via provider errors");
        return Results.Problem(
            title: "LLM provider error",
            detail: "LLM service is temporarily unavailable",
            statusCode: StatusCodes.Status502BadGateway);
    }

    private static async Task<IResult> RunAgentLoopAsync(
        ChatRequest request,
        string requestId,
        string modelKey,
        IAzureOpenAIClient client,
        IQueryService queryService,
        object[] tools,
        string systemPrompt,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var messages = new List<object>
        {
            new { role = "system", content = systemPrompt },
            new { role = "user",   content = request.Message }
        };

        AzureUsage? totalUsage = null;
        string model = string.Empty;

        for (int iteration = 0; iteration < MaxToolIterations; iteration++)
        {
            logger.LogInformation("Agent loop iteration {Iteration}. ModelKey={ModelKey}", iteration + 1, modelKey);

            var raw = await client.GetRawCompletionAsync(messages, tools, modelKey, cancellationToken);

            model = raw.Model;
            totalUsage = raw.Usage;

            if (raw.FinishReason == "stop")
            {
                logger.LogInformation("Agent loop complete. Iterations={Iterations}, TotalTokens={Tokens}",
                    iteration + 1, raw.Usage?.Total_tokens ?? 0);

                return Results.Ok(new ChatResponse
                {
                    Reply = raw.Content ?? string.Empty,
                    Model = model,
                    Usage = raw.Usage != null ? new UsageInfo
                    {
                        PromptTokens = raw.Usage.Prompt_tokens,
                        CompletionTokens = raw.Usage.Completion_tokens,
                        TotalTokens = raw.Usage.Total_tokens
                    } : null,
                    RequestId = requestId
                });
            }

            if (raw.FinishReason == "tool_calls" && raw.ToolCalls is { Count: > 0 })
            {
                // Lisää assistant-viesti tool_calls:lla (serialisoidaan sellaisenaan)
                messages.Add(new { role = "assistant", content = raw.Content, tool_calls = raw.ToolCalls });

                foreach (var tc in raw.ToolCalls)
                {
                    string toolResult;
                    try
                    {
                        toolResult = await ExecuteToolAsync(tc, queryService, logger, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Tool {ToolName} failed. ToolCallId={Id}", tc.Function.Name, tc.Id);
                        toolResult = $"Tool execution error: {ex.Message}";
                    }

                    messages.Add(new { role = "tool", tool_call_id = tc.Id, content = toolResult });

                    logger.LogInformation("Tool executed. Name={Name}, ResultLength={Length}",
                        tc.Function.Name, toolResult.Length);
                }

                continue;
            }

            // Tuntematon finish_reason — lopeta silmukka
            logger.LogWarning("Unexpected finish_reason={FinishReason}, ending loop", raw.FinishReason);
            break;
        }

        logger.LogError("Agent loop exceeded MaxToolIterations={Max}", MaxToolIterations);
        return Results.Problem(
            title: "LLM agent loop error",
            detail: "Maximum tool iterations exceeded",
            statusCode: StatusCodes.Status500InternalServerError);
    }

    private static async Task<string> ExecuteToolAsync(
        AzureToolCall tc,
        IQueryService queryService,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        using var argsDoc = JsonDocument.Parse(tc.Function.Arguments);
        var args = argsDoc.RootElement;

        return tc.Function.Name switch
        {
            "query_database" => await queryService.ExecuteQueryAsync(
                args.GetProperty("sql").GetString()
                    ?? throw new InvalidOperationException("query_database: 'sql' argument missing"),
                cancellationToken),

            _ => throw new InvalidOperationException($"Unknown tool: {tc.Function.Name}")
        };
    }
}
