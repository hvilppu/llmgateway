using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Extensions.Timer;
using Microsoft.Extensions.Logging;
using SyncFunction.Services;

namespace SyncFunction;

// Timer trigger joka synkronoi Cosmos DB:n muutokset MS SQL:ään 15 minuutin välein.
// MigrationService huolehtii taulujen luomisesta ennen ensimmäistä ajoa.
public class CosmosToSqlTrigger
{
    private readonly CosmosSyncService _syncService;
    private readonly ILogger<CosmosToSqlTrigger> _logger;

    public CosmosToSqlTrigger(CosmosSyncService syncService, ILogger<CosmosToSqlTrigger> logger)
    {
        _syncService = syncService;
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

        await _syncService.SyncAsync(cancellationToken);
    }
}
