using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SyncFunction.Services;

// Azure Functions Isolated Worker -käynnistysohjelma.
// MigrationService ajaa SQL-migraatiot heti käynnistyksen yhteydessä IHostedService-mekanismilla.

var builder = FunctionsApplication.CreateBuilder(args);

// Application Insights -integraatio (Worker v2: ConfigureFunctionsWorkerDefaults poistettu, sisäänrakennettu)
builder.Services.AddApplicationInsightsTelemetryWorkerService();

// Cosmos DB -asiakas — yksittäinen instanssi koko funktiosovellukselle (thread-safe)
builder.Services.AddSingleton(sp =>
{
    var connStr = builder.Configuration["CosmosRag:ConnectionString"]
        ?? throw new InvalidOperationException("CosmosRag:ConnectionString puuttuu konfiguraatiosta");
    return new CosmosClient(connStr);
});

// Konfiguraatio-optiot
builder.Services.Configure<CosmosOptions>(builder.Configuration.GetSection("CosmosRag"));
builder.Services.Configure<SqlOptions>(builder.Configuration.GetSection("Sql"));
builder.Services.Configure<MonthlyReportOptions>(builder.Configuration.GetSection("MonthlyReport"));

// IHttpClientFactory — MonthlyReportService käyttää Azure OpenAI -kutsuihin
builder.Services.AddHttpClient();

// Synkronointi- ja kuukausiraporttipalvelut — injektoidaan triggeriin
builder.Services.AddSingleton<CosmosSyncService>();
builder.Services.AddSingleton<MonthlyReportService>();

// Backfill käynnistyksessä: generoi raportit kaikille historiakuukausille
builder.Services.AddHostedService<ReportBackfillService>();

// Migraatiot ajetaan IHostedService-toteutuksena heti käynnistyksen yhteydessä
builder.Services.AddHostedService<MigrationService>();

builder.Build().Run();
