using LlmGateway;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// API-avainautentikointi
builder.Services.Configure<ApiKeyOptions>(
    builder.Configuration.GetSection("ApiKey"));

// Sido optiot konfiguraatiosta
builder.Services.Configure<AzureOpenAIOptions>(
    builder.Configuration.GetSection("AzureOpenAI"));
builder.Services.Configure<CircuitBreakerOptions>(
    builder.Configuration.GetSection("CircuitBreaker"));
builder.Services.Configure<PolicyOptions>(options =>
{
    var section = builder.Configuration.GetSection("Policies");
    options.Policies = section.GetChildren()
        .ToDictionary(
            s => s.Key,
            s => s.Get<PolicyConfig>() ?? new PolicyConfig());
});

// RAG — Cosmos DB vektorihaulla
builder.Services.Configure<CosmosRagOptions>(
    builder.Configuration.GetSection("CosmosRag"));
builder.Services.AddSingleton<CosmosClient>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<CosmosRagOptions>>().Value;
    return new CosmosClient(opts.ConnectionString);
});
builder.Services.AddSingleton<IRagService, CosmosRagService>();
builder.Services.AddSingleton<IQueryService, CosmosQueryService>();

// Circuit breaker — singleton jotta tila säilyy kutsujen välillä
builder.Services.AddSingleton<ICircuitBreaker, InMemoryCircuitBreaker>();

// Routing engine — singleton, lukee konfigin kerran
builder.Services.AddSingleton<IRoutingEngine, RoutingEngine>();

// HttpClient + tyypitetty client
builder.Services.AddHttpClient<IAzureOpenAIClient, AzureOpenAIClient>(client =>
{
    // BaseAddress ja headerit asetetaan itse client-luokassa
});

// Lisää loggaus
builder.Services.AddLogging();

// Application Insights — lukee connection stringin APPLICATIONINSIGHTS_CONNECTION_STRING-muuttujasta
builder.Services.AddApplicationInsightsTelemetry();

// http://localhost:5079/openapi/v1.json
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseMiddleware<ApiKeyMiddleware>();

app.MapChatEndpoints();

app.Run();