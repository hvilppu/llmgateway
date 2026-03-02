using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SyncFunction.Services;

// MS SQL -yhteysasetukset. Sidotaan appsettings-osioon "Sql".
public class SqlOptions
{
    public string ConnectionString { get; set; } = string.Empty;
}

// Ajaa idempotentit SQL-migraatiot käynnistyksen yhteydessä.
// Kaikki lauseet ovat IF NOT EXISTS -suojattuja — turvallinen ajaa uudelleen.
public class MigrationService : IHostedService
{
    private readonly SqlOptions _options;
    private readonly ILogger<MigrationService> _logger;

    public MigrationService(IOptions<SqlOptions> options, ILogger<MigrationService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Ajetaan SQL-migraatiot...");

        await using var conn = new SqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        // Migraatio 1: mittaukset-taulu — pääasiallinen kohdetaulu synkronoinnille
        await ExecuteMigrationAsync(conn, """
            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'mittaukset')
            BEGIN
                CREATE TABLE mittaukset (
                    id          NVARCHAR(50)  NOT NULL PRIMARY KEY,
                    paikkakunta NVARCHAR(100) NOT NULL,
                    pvm         DATE          NOT NULL,
                    lampotila   FLOAT         NOT NULL
                );
                CREATE INDEX IX_mitt_paikkakunta_pvm ON mittaukset (paikkakunta, pvm);
            END
            """, cancellationToken);

        // Migraatio 2: sync_state-taulu — tallentaa viimeksi synkronoidun _ts-vesimerkin
        await ExecuteMigrationAsync(conn, """
            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'sync_state')
            BEGIN
                CREATE TABLE sync_state (last_synced_ts BIGINT NOT NULL DEFAULT 0);
                INSERT INTO sync_state (last_synced_ts) VALUES (0);
            END
            """, cancellationToken);

        _logger.LogInformation("SQL-migraatiot valmis.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task ExecuteMigrationAsync(SqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, conn);
        cmd.CommandTimeout = 30;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
