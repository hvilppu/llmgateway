using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SyncFunction.Services;

// Azure Functions Isolated Worker -käynnistysohjelma.
// MigrationService ajaa SQL-migraatiot heti käynnistyksen yhteydessä IHostedService-mekanismilla.

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWorkerDefaults();

// Application Insights -integraatio
builder.Services.AddApplicationInsightsTelemetryWorkerService();
builder.Services.ConfigureFunctionsApplicationInsights();

// Cosmos DB -asiakas — yksittäinen instanssi koko funktiosovellukselle (thread-safe)
builder.Services.AddSingleton(sp =>
{
    var connStr = builder.Configuration["CosmosRag__ConnectionString"]
        ?? throw new InvalidOperationException("CosmosRag__ConnectionString puuttuu konfiguraatiosta");
    return new CosmosClient(connStr);
});

// Konfiguraatio-optiot
builder.Services.Configure<CosmosRagOptions>(builder.Configuration.GetSection("CosmosRag"));
builder.Services.Configure<SqlOptions>(builder.Configuration.GetSection("Sql"));

// Synkronointipalvelu — injektoidaan triggeriin
builder.Services.AddSingleton<CosmosSyncService>();

// Migraatiot ajetaan IHostedService-toteutuksena heti käynnistyksen yhteydessä
builder.Services.AddHostedService<MigrationService>();

builder.Build().Run();
