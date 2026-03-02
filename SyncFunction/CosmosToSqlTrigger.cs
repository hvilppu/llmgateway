using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Extensions.Timer;
using Microsoft.Extensions.Logging;
using SyncFunction.Services;

namespace SyncFunction;

// Timer trigger joka synkronoi Cosmos DB:n muutokset MS SQL:ään 15 minuutin välein
// ja päivittää kuukausiraportit RAG-hakua varten.
// MigrationService huolehtii taulujen luomisesta ennen ensimmäistä ajoa.
public class CosmosToSqlTrigger
{
    private readonly CosmosSyncService _syncService;
    private readonly MonthlyReportService _monthlyReportService;
    private readonly ILogger<CosmosToSqlTrigger> _logger;

    public CosmosToSqlTrigger(
        CosmosSyncService syncService,
        MonthlyReportService monthlyReportService,
        ILogger<CosmosToSqlTrigger> logger)
    {
        _syncService = syncService;
        _monthlyReportService = monthlyReportService;
        _logger = logger;
    }

    // Cron-lauseke: "0 */15 * * * *" = joka 15. minuutti (sekuntitarkkuus käytössä)
    [Function("CosmosToSqlTimer")]
    public async Task Run(
        [TimerTrigger("0 */15 * * * *")] TimerInfo myTimer,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "CosmosToSqlTimer käynnistyi. SeuraavaAjo={Next}",
            myTimer.ScheduleStatus?.Next);

        var syncedCount = await _syncService.SyncAsync(cancellationToken);

        // Kuukausiraportit päivitetään vain jos uusia mittauksia tuli — säästää OpenAI-kutsut
        if (syncedCount > 0)
            await _monthlyReportService.GenerateCurrentMonthReportsAsync(cancellationToken);
    }
}
