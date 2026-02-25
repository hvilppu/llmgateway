namespace LlmGateway;

public static class ChatEndpoints
{

    public static void MapChatEndpoints(this WebApplication app)
    {
        app.MapPost("/api/chat", async (
            ChatRequest request,
            IAzureOpenAIClient client,
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

            logger.LogInformation("Received chat request. MessageLength={MessageLength}", request.Message.Length);

            try
            {
                var response = await client.GetChatCompletionAsync(request, requestId, cancellationToken);

                logger.LogInformation("Chat request handled successfully. TotalTokens={TotalTokens}",
                    response.Usage?.TotalTokens ?? 0);

                return Results.Ok(response);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error handling chat request");
                return Results.Problem(
                    title: "LLM gateway error",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status502BadGateway);
            }
        })
        .WithName("ChatCompletion");
    }
}
