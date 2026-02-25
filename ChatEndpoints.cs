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
                // Valitaan oikea malli pyynnön policyn perusteella
                var modelKey = routingEngine.ResolveModelKey(request);

                var response = await client.GetChatCompletionAsync(request, requestId, modelKey, cancellationToken);

                logger.LogInformation("Chat request handled successfully. ModelKey={ModelKey}, TotalTokens={TotalTokens}",
                    modelKey, response.Usage?.TotalTokens ?? 0);

                return Results.Ok(response);
            }
            catch (CircuitBreakerOpenException ex)
            {
                // Piiri auki — Azure on toistuvasti epäonnistunut, ei edes yritetä
                logger.LogWarning(ex, "Circuit breaker open, returning 503 for model {ModelKey}", ex.ModelKey);
                return Results.Problem(
                    title: "LLM model temporarily unavailable",
                    detail: $"Circuit breaker is open for model {ex.ModelKey}",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (HttpRequestException ex)
            {
                // Azure vastasi virheellä tai ei vastannut
                logger.LogError(ex, "Downstream LLM provider error");
                return Results.Problem(
                    title: "LLM provider error",
                    detail: ex.Message,
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
