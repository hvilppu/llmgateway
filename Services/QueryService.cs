using Microsoft.Azure.Cosmos;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using System.Text.Json;

namespace LlmGateway.Services;

// Text-to-SQL -palvelun rajapinta. LLM generoi SQL-kyselyn, gateway suorittaa sen tietokannassa.
public interface IQueryService
{
    // Suorittaa SELECT-kyselyn ja palauttaa tulokset JSON-arrayna.
    // Heittää InvalidOperationException jos kysely ei ole SELECT.
    Task<string> ExecuteQueryAsync(string sql, CancellationToken cancellationToken = default);
}

// Cosmos DB -yhteysasetukset. Sidotaan appsettings-osioon "CosmosRag".
public class CosmosOptions
{
    public string ConnectionString { get; set; } = string.Empty;
    public string DatabaseName { get; set; } = string.Empty;
    public string ContainerName { get; set; } = string.Empty;
    // Palautettavien rivien enimmäismäärä — estää liian suurten tulosjoukkojen palautuksen
    public int MaxRows { get; set; } = 500;
}

// MS SQL -yhteysasetukset.
public class SqlOptions
{
    public string ConnectionString { get; set; } = string.Empty;
    // Palautettavien rivien enimmäismäärä — estää liian suurten tulosjoukkojen palautuksen
    public int MaxRows { get; set; } = 500;
}

// Suorittaa LLM:n generoimia T-SQL SELECT -kyselyitä Azure SQL / MS SQL Server -kantaan.
public class SqlQueryService : IQueryService
{
    private readonly SqlOptions _options;
    private readonly ILogger<SqlQueryService> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public SqlQueryService(IOptions<SqlOptions> options, ILogger<SqlQueryService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string> ExecuteQueryAsync(string sql, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sql))
            throw new ArgumentException("SQL query cannot be empty", nameof(sql));

        // Turvallisuustarkistus: parsitaan T-SQL AST:ksi ja varmistetaan että on tasan yksi SELECT-lause
        ValidateSingleSelect(sql);

        _logger.LogInformation("Executing MS SQL query. QuerySnippet={Snippet}",
            sql.Length > 200 ? sql[..200] : sql);

        var rows = new List<Dictionary<string, object?>>();
        var maxRows = _options.MaxRows;

        await using var conn = new SqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.CommandTimeout = 30;

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            if (rows.Count >= maxRows)
            {
                _logger.LogWarning("Query result truncated at {MaxRows} rows", maxRows);
                break;
            }
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }

        _logger.LogInformation("Query returned {Count} rows", rows.Count);

        return JsonSerializer.Serialize(rows, JsonOpts);
    }

    // Kielletyt merkkijonot — AST ei pura XQuery-lausekkeita, joten tarkistus tehdään tekstinä.
    // xs:base64Binary / xs:hexBinary: datan enkoodaus XML-metodikutsuissa
    // OPENROWSET: ulkoisten tiedostojen tai lähteiden luku SELECT:n sisältä
    // OPENQUERY: linkitetyn palvelimen kyselyt SELECT:n sisältä
    private static readonly string[] BlockedPatterns =
    [
        "xs:base64Binary",
        "xs:hexBinary",
        "OPENROWSET",
        "OPENQUERY",
    ];

    // Parsii T-SQL AST:ksi ja varmistaa että kyselyssä on tasan yksi SELECT-lause.
    // Hylkää mm. useamman lauseen (puolipiste), EXEC, INSERT, UPDATE, DELETE, DROP, CREATE,
    // sys-skeeman viittaukset (sys.tables, [sys].[columns] jne.) sekä kielletyt merkkijonot.
    private static void ValidateSingleSelect(string sql)
    {
        // Kielletyt merkkijonot tarkistetaan ennen AST-parsintaa
        foreach (var pattern in BlockedPatterns)
        {
            if (sql.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Blocked SQL pattern: {pattern}");
        }

        var parser = new TSql160Parser(initialQuotedIdentifiers: false);
        var fragment = parser.Parse(new StringReader(sql), out var errors);

        if (errors.Count > 0)
            throw new InvalidOperationException($"SQL parse error: {errors[0].Message}");

        var script = (TSqlScript)fragment;

        if (script.Batches.Count != 1 || script.Batches[0].Statements.Count != 1)
            throw new InvalidOperationException("Only a single SELECT statement is allowed");

        if (script.Batches[0].Statements[0] is not SelectStatement)
            throw new InvalidOperationException("Only SELECT statements are allowed");

        // Käy AST läpi ja estä sys-skeeman viittaukset
        var visitor = new BlockedSchemaVisitor();
        fragment.Accept(visitor);
        if (visitor.BlockedSchemaFound)
            throw new InvalidOperationException("Queries against sys schema are not allowed");
    }

    // AST-vierailija joka etsii kiellettyä skeemaa (sys) tauluviittauksista.
    // Toimii myös hakasulkusyntaksilla: [sys].[tables] → SchemaIdentifier.Value = "sys".
    private sealed class BlockedSchemaVisitor : TSqlFragmentVisitor
    {
        public bool BlockedSchemaFound { get; private set; }

        public override void Visit(SchemaObjectName node)
        {
            if (node.SchemaIdentifier?.Value != null &&
                node.SchemaIdentifier.Value.Equals("sys", StringComparison.OrdinalIgnoreCase))
            {
                BlockedSchemaFound = true;
            }
        }
    }
}

// Suorittaa LLM:n generoimia Cosmos DB SQL -kyselyitä.
public class CosmosQueryService : IQueryService
{
    private readonly CosmosClient _cosmosClient;
    private readonly CosmosOptions _options;
    private readonly ILogger<CosmosQueryService> _logger;

    public CosmosQueryService(
        CosmosClient cosmosClient,
        IOptions<CosmosOptions> options,
        ILogger<CosmosQueryService> logger)
    {
        _cosmosClient = cosmosClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string> ExecuteQueryAsync(string sql, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sql))
            throw new ArgumentException("SQL query cannot be empty", nameof(sql));

        // Turvallisuustarkistus: vain SELECT-kyselyt sallittu
        var trimmed = sql.TrimStart();
        if (!trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only SELECT queries are allowed in query_database tool");

        _logger.LogInformation("Executing Cosmos DB query. QuerySnippet={Snippet}",
            sql.Length > 200 ? sql[..200] : sql);

        var container = _cosmosClient
            .GetDatabase(_options.DatabaseName)
            .GetContainer(_options.ContainerName);

        var queryDef = new QueryDefinition(sql);
        var rows = new List<string>();
        var maxRows = _options.MaxRows;
        var truncated = false;

        using var feed = container.GetItemQueryStreamIterator(queryDef);

        while (feed.HasMoreResults && !truncated)
        {
            using var response = await feed.ReadNextAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(response.Content, cancellationToken: cancellationToken);

            if (doc.RootElement.TryGetProperty("Documents", out var documents))
            {
                foreach (var item in documents.EnumerateArray())
                {
                    if (rows.Count >= maxRows)
                    {
                        _logger.LogWarning("Query result truncated at {MaxRows} rows", maxRows);
                        truncated = true;
                        break;
                    }
                    rows.Add(item.GetRawText());
                }
            }
        }

        _logger.LogInformation("Query returned {Count} rows", rows.Count);

        return "[" + string.Join(",", rows) + "]";
    }
}
