using LlmGateway;

var builder = WebApplication.CreateBuilder(args);

// Bind options
builder.Services.Configure<AzureOpenAIOptions>(
    builder.Configuration.GetSection("AzureOpenAI"));
builder.Services.Configure<CircuitBreakerOptions>(
    builder.Configuration.GetSection("CircuitBreaker"));

// Circuit breaker — singleton jotta tila säilyy kutsujen välillä
builder.Services.AddSingleton<ICircuitBreaker, InMemoryCircuitBreaker>();

// HttpClient + typed client
builder.Services.AddHttpClient<IAzureOpenAIClient, AzureOpenAIClient>(client =>
{
    // BaseAddress ja headerit asetetaan itse client-luokassa
});

// Add logging etc.
builder.Services.AddLogging();

// http://localhost:5079/openapi/v1.json
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.MapChatEndpoints();

app.Run();