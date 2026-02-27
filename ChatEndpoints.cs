namespace LlmGateway;

public static class ChatEndpoints
{
    public static void MapChatEndpoints(this WebApplication app)
    {
        app.MapPost("/api/chat", async (
            ChatRequest request,
            IAzureOpenAIClient client,
            IRoutingEngine routingEngine,
            ILoggerFactory loggerFactory,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var logger = loggerFactory.CreateLogger("LlmGateway.ChatEndpoint");

            // Luo korrelaatio-id (tai käytä esim. X-Correlation-ID -headeria)
            var requestId = httpContext.TraceIdentifier;

            using var scope = logger.BeginScope(new Dictionary<string, object>
            {
                ["requestId"] = requestId
            });

            logger.LogInformation("Received chat request. Policy={Policy}, MessageLength={MessageLength}",
                request.Policy ?? "chat_default", request.Message.Length);

            try
            {
                // Haetaan järjestetty lista malleista: [primary, fallback1, ...]
                var modelChain = routingEngine.ResolveModelChain(request);
                ChatResponse? response = null;
                Exception? lastException = null;

                foreach (var modelKey in modelChain)
                {
                    try
                    {
                        response = await client.GetChatCompletionAsync(request, requestId, modelKey, cancellationToken);

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

                // Kaikki mallit epäonnistui
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
                    detail: lastException?.Message ?? "All models failed",
                    statusCode: StatusCodes.Status502BadGateway);
            }
            catch (Exception ex)
            {
                // Odottamaton virhe — ei vuodeta yksityiskohtia asiakkaalle
                logger.LogError(ex, "Unexpected error handling chat request");
                return Results.Problem(
                    title: "LLM gateway error",
                    detail: "Unexpected error occurred",
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        })
        .WithName("ChatCompletion");
    }
}
