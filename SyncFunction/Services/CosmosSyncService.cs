using Microsoft.Azure.Cosmos;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace SyncFunction.Services;

// Cosmos DB -yhteysasetukset. Sidotaan appsettings-osioon "CosmosRag".
public class CosmosOptions
{
    public string ConnectionString { get; set; } = string.Empty;
    public string DatabaseName { get; set; } = string.Empty;
    public string ContainerName { get; set; } = string.Empty;
}

// Synkronoi Cosmos DB:n muutokset MS SQL:ään _ts-vesimerkin avulla.
// Muutosdetektio: SELECT * FROM c WHERE c._ts > @lastTs ORDER BY c._ts ASC
// Upsert: MERGE INTO mittaukset (idempotentti)
// Tila: last_synced_ts tallennetaan sync_state-tauluun
public class CosmosSyncService
{
    private readonly CosmosClient _cosmosClient;
    private readonly CosmosOptions _cosmosOptions;
    private readonly SqlOptions _sqlOptions;
    private readonly ILogger<CosmosSyncService> _logger;

    public CosmosSyncService(
        CosmosClient cosmosClient,
        IOptions<CosmosOptions> cosmosOptions,
        IOptions<SqlOptions> sqlOptions,
        ILogger<CosmosSyncService> logger)
    {
        _cosmosClient = cosmosClient;
        _cosmosOptions = cosmosOptions.Value;
        _sqlOptions = sqlOptions.Value;
        _logger = logger;
    }

    // Palauttaa synkronoitujen dokumenttien määrän (0 = ei uusia).
    public async Task<int> SyncAsync(CancellationToken cancellationToken)
    {
        // 1. Lue edellinen vesimerkki SQL:stä
        long lastSyncedTs = await ReadLastSyncedTsAsync(cancellationToken);
        _logger.LogInformation("Synkronointi alkaa. LastSyncedTs={Ts}", lastSyncedTs);

        // 2. Hae Cosmos-dokumentit joiden _ts > lastSyncedTs, järjestyksessä vanhimmasta uusimpaan
        var docs = await FetchCosmosDocumentsAsync(lastSyncedTs, cancellationToken);

        if (docs.Count == 0)
        {
            _logger.LogInformation("Ei uusia dokumentteja synkronoitavana.");
            return 0;
        }

        _logger.LogInformation("Löydetty {Count} synkronoitavaa dokumenttia.", docs.Count);

        // 3. Upsertaa SQL:ään ja päivitä vesimerkki atomisesti samassa transaktiossa
        await UpsertToSqlAsync(docs, cancellationToken);
        return docs.Count;
    }

    // Lukee last_synced_ts-arvon sync_state-taulusta.
    // Palauttaa 0 jos taulu on tyhjä (ensimmäinen ajo ennen migraatiota).
    private async Task<long> ReadLastSyncedTsAsync(CancellationToken ct)
    {
        await using var conn = new SqlConnection(_sqlOptions.ConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand("SELECT TOP 1 last_synced_ts FROM sync_state", conn);
        cmd.CommandTimeout = 10;

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long ts ? ts : (result is int i ? (long)i : 0L);
    }

    // Hakee Cosmos-dokumentit joiden _ts on suurempi kuin edellinen vesimerkki.
    // Ohittaa dokumentit joista puuttuu pakolliset kentät (kirjataan varoitus).
    private async Task<List<CosmosDocument>> FetchCosmosDocumentsAsync(long lastTs, CancellationToken ct)
    {
        var container = _cosmosClient
            .GetDatabase(_cosmosOptions.DatabaseName)
            .GetContainer(_cosmosOptions.ContainerName);

        // Järjestys _ts:n mukaan takaa että vesimerkki vastaa viimeistä käsiteltyä dokumenttia
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c._ts > @lastTs ORDER BY c._ts ASC")
            .WithParameter("@lastTs", lastTs);

        var docs = new List<CosmosDocument>();
        using var feed = container.GetItemQueryStreamIterator(query);

        while (feed.HasMoreResults)
        {
            using var response = await feed.ReadNextAsync(ct);
            using var doc = await JsonDocument.ParseAsync(response.Content, cancellationToken: ct);

            if (!doc.RootElement.TryGetProperty("Documents", out var documents))
                continue;

            foreach (var item in documents.EnumerateArray())
            {
                var parsed = ParseDocument(item);
                if (parsed is not null)
                {
                    docs.Add(parsed);
                }
                else
                {
                    var rawId = item.TryGetProperty("id", out var idProp) ? idProp.GetString() : "?";
                    _logger.LogWarning("Ohitetaan dokumentti puuttuvien kenttien takia. Id={Id}", rawId);
                }
            }
        }

        return docs;
    }

    // Poimii mittaukset-rivin tiedot Cosmos-dokumentista.
    // Tukee kahta rakennetta — sama logiikka kuin seed_mssql.py:ssä:
    //   - kentät content-aliobjektissa: { id, content: { paikkakunta, pvm, lampotila } }
    //   - kentät ylätasolla: { id, paikkakunta, pvm, lampotila }
    private static CosmosDocument? ParseDocument(JsonElement element)
    {
        if (!element.TryGetProperty("id", out var idProp)) return null;
        if (!element.TryGetProperty("_ts", out var tsProp)) return null;

        var id = idProp.GetString();
        if (string.IsNullOrWhiteSpace(id)) return null;

        long ts = tsProp.GetInt64();

        string? paikkakunta = null;
        string? pvm = null;
        double? lampotila = null;

        // Kokeile ensin content-aliobjekti (seed_cosmos.py käyttää tätä rakennetta)
        if (element.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object)
        {
            paikkakunta = TryGetString(content, "paikkakunta");
            pvm = TryGetString(content, "pvm");
            lampotila = TryGetDouble(content, "lampotila") ?? TryGetDouble(content, "lämpötila");
        }

        // Fallback: ylätason kentät
        paikkakunta ??= TryGetString(element, "paikkakunta");
        pvm ??= TryGetString(element, "pvm");
        lampotila ??= TryGetDouble(element, "lampotila") ?? TryGetDouble(element, "lämpötila");

        if (paikkakunta is null || pvm is null || lampotila is null)
            return null;

        return new CosmosDocument(id, paikkakunta, pvm, lampotila.Value, ts);
    }

    private static string? TryGetString(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static double? TryGetDouble(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return null;
        return v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
    }

    // Upsertaa kaikki dokumentit mittaukset-tauluun ja päivittää last_synced_ts
    // samassa transaktiossa — atomisuus takaa että vesimerkki ei etene osittaisen batchin yli.
    private async Task UpsertToSqlAsync(List<CosmosDocument> docs, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_sqlOptions.ConnectionString);
        await conn.OpenAsync(ct);
        await using var tx = (Microsoft.Data.SqlClient.SqlTransaction)await conn.BeginTransactionAsync(ct);

        try
        {
            int ok = 0;
            long maxTs = 0;

            foreach (var doc in docs)
            {
                // MERGE-upsert: päivittää olemassaolevan tai lisää uuden rivin
                await using var cmd = new SqlCommand("""
                    MERGE INTO mittaukset AS target
                    USING (VALUES (@id, @paikkakunta, @pvm, @lampotila))
                          AS source (id, paikkakunta, pvm, lampotila)
                    ON target.id = source.id
                    WHEN MATCHED THEN
                        UPDATE SET
                            paikkakunta = source.paikkakunta,
                            pvm         = source.pvm,
                            lampotila   = source.lampotila
                    WHEN NOT MATCHED THEN
                        INSERT (id, paikkakunta, pvm, lampotila)
                        VALUES (source.id, source.paikkakunta, source.pvm, source.lampotila);
                    """, conn, tx);

                cmd.Parameters.AddWithValue("@id", doc.Id);
                cmd.Parameters.AddWithValue("@paikkakunta", doc.Paikkakunta);
                cmd.Parameters.AddWithValue("@pvm", doc.Pvm);
                cmd.Parameters.AddWithValue("@lampotila", doc.Lampotila);
                cmd.CommandTimeout = 30;

                await cmd.ExecuteNonQueryAsync(ct);
                ok++;
                if (doc.Ts > maxTs) maxTs = doc.Ts;
            }

            // Päivitä vesimerkki — seuraava ajo hakee vain tätä uudemmat dokumentit
            if (maxTs > 0)
            {
                await using var updateCmd = new SqlCommand(
                    "UPDATE sync_state SET last_synced_ts = @ts", conn, tx);
                updateCmd.Parameters.AddWithValue("@ts", maxTs);
                updateCmd.CommandTimeout = 10;
                await updateCmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
            _logger.LogInformation(
                "Synkronointi valmis. Upsertattuja={Ok}, MaxTs={MaxTs}", ok, maxTs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Synkronointi epäonnistui — perutaan transaktio.");
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // Cosmos-dokumentin sisäinen esitys synkronoinnin aikana.
    private record CosmosDocument(
        string Id,
        string Paikkakunta,
        string Pvm,
        double Lampotila,
        long Ts);
}
