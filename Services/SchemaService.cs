using Microsoft.Azure.Cosmos;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;

namespace LlmGateway.Services;

// Hakee tietokannan skeematiedot dynaamisesti ja välimuistittaa ne.
// Käytetään system promptin rakentamiseen — LLM saa ajan tasalla olevan skeeman.
public interface ISchemaProvider
{
    Task<string> GetSchemaAsync(CancellationToken cancellationToken = default);
}

// Hakee MS SQL -skeeman INFORMATION_SCHEMA.COLUMNS -näkymästä.
// Välimuistittaa tuloksen 60 minuutiksi. Epäonnistuminen palauttaa tyhjän merkkijonon.
public class SqlSchemaProvider : ISchemaProvider
{
    private readonly SqlOptions _options;
    private readonly ILogger<SqlSchemaProvider> _logger;

    // Välimuisti ja sen vanhentumisaika — volatile takaa näkyvyyden eri säikeille
    private volatile string? _cachedSchema;
    private DateTime _cacheExpiry;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(60);

    public SqlSchemaProvider(IOptions<SqlOptions> options, ILogger<SqlSchemaProvider> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string> GetSchemaAsync(CancellationToken cancellationToken = default)
    {
        // Nopea polku: ei lukkoa jos välimuisti on voimassa
        if (_cachedSchema != null && DateTime.UtcNow < _cacheExpiry)
            return _cachedSchema;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            // Double-check lukon saamisen jälkeen
            if (_cachedSchema != null && DateTime.UtcNow < _cacheExpiry)
                return _cachedSchema;

            try
            {
                var schema = await FetchSchemaAsync(cancellationToken);
                _cachedSchema = schema;
                _cacheExpiry = DateTime.UtcNow.Add(CacheDuration);
                _logger.LogInformation("MS SQL schema fetched and cached. Length={Length}", schema.Length);
                return schema;
            }
            catch (Exception ex)
            {
                // Epäonnistuminen ei kaadi pyyntöä — system prompt rakentuu ilman skeemaa
                _logger.LogWarning(ex, "MS SQL schema fetch failed, falling back to empty schema");
                return string.Empty;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<string> FetchSchemaAsync(CancellationToken cancellationToken)
    {
        await using var conn = new SqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = new SqlCommand("""
            SELECT TABLE_NAME, COLUMN_NAME, DATA_TYPE
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = 'dbo'
            ORDER BY TABLE_NAME, ORDINAL_POSITION
            """, conn);
        cmd.CommandTimeout = 10;

        var sb = new StringBuilder();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        string? currentTable = null;

        while (await reader.ReadAsync(cancellationToken))
        {
            var table = reader.GetString(0);
            if (table != currentTable)
            {
                if (currentTable != null) sb.AppendLine();
                sb.AppendLine($"Table: {table}");
                currentTable = table;
            }
            sb.AppendLine($"  {reader.GetString(1)} ({reader.GetString(2)})");
        }

        return sb.ToString().TrimEnd();
    }
}

// Hakee Cosmos DB -containerin skeeman näytedokumentista (SELECT TOP 1 * FROM c).
// Schematon kanta — kenttänimet ja tyypit päätellään rekursiivisesti esimerkkidokumentista.
// Välimuistittaa tuloksen 60 minuutiksi. Epäonnistuminen palauttaa tyhjän merkkijonon.
public class CosmosSchemaProvider : ISchemaProvider
{
    private readonly CosmosClient _cosmosClient;
    private readonly CosmosOptions _options;
    private readonly ILogger<CosmosSchemaProvider> _logger;

    private volatile string? _cachedSchema;
    private DateTime _cacheExpiry;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(60);

    public CosmosSchemaProvider(
        CosmosClient cosmosClient,
        IOptions<CosmosOptions> options,
        ILogger<CosmosSchemaProvider> logger)
    {
        _cosmosClient = cosmosClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string> GetSchemaAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedSchema != null && DateTime.UtcNow < _cacheExpiry)
            return _cachedSchema;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_cachedSchema != null && DateTime.UtcNow < _cacheExpiry)
                return _cachedSchema;

            try
            {
                var schema = await FetchSchemaAsync(cancellationToken);
                _cachedSchema = schema;
                _cacheExpiry = DateTime.UtcNow.Add(CacheDuration);
                _logger.LogInformation("Cosmos DB schema fetched and cached. Length={Length}", schema.Length);
                return schema;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cosmos DB schema fetch failed, falling back to empty schema");
                return string.Empty;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<string> FetchSchemaAsync(CancellationToken cancellationToken)
    {
        var container = _cosmosClient
            .GetDatabase(_options.DatabaseName)
            .GetContainer(_options.ContainerName);

        using var feed = container.GetItemQueryStreamIterator(
            new QueryDefinition("SELECT TOP 1 * FROM c"));

        if (!feed.HasMoreResults)
            return string.Empty;

        using var response = await feed.ReadNextAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(response.Content, cancellationToken: cancellationToken);

        if (!doc.RootElement.TryGetProperty("Documents", out var documents))
            return string.Empty;

        var sample = documents.EnumerateArray().FirstOrDefault();
        if (sample.ValueKind == JsonValueKind.Undefined)
            return string.Empty;

        // Muodostetaan skeemakuvaus rekursiivisesti näytedokumentista
        var sb = new StringBuilder();
        sb.AppendLine($"Container: {_options.ContainerName}");
        FlattenJsonPaths(sample, "c", sb);
        return sb.ToString().TrimEnd();
    }

    // Rekursiivisesti listaa JSON-polut ja tyypit näytedokumentista.
    // Sisäkkäiset objektit puretaan pistenotaatiolla (c.content.paikkakunta).
    private static void FlattenJsonPaths(JsonElement element, string prefix, StringBuilder sb)
    {
        foreach (var prop in element.EnumerateObject())
        {
            var path = $"{prefix}.{prop.Name}";
            if (prop.Value.ValueKind == JsonValueKind.Object)
                FlattenJsonPaths(prop.Value, path, sb);
            else
                sb.AppendLine($"  {path} ({JsonKindToType(prop.Value.ValueKind)})");
        }
    }

    private static string JsonKindToType(JsonValueKind kind) => kind switch
    {
        JsonValueKind.String                      => "string",
        JsonValueKind.Number                      => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Array                       => "array",
        JsonValueKind.Null                        => "null",
        _                                         => "unknown"
    };
}
