using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace LlmGateway;

// Text-to-NoSQL -palvelun rajapinta. LLM generoi SQL-kyselyn, gateway suorittaa sen Cosmos DB:ssä.
public interface IQueryService
{
    // Suorittaa Cosmos DB SQL -kyselyn ja palauttaa tulokset JSON-arrayna.
    // Heittää InvalidOperationException jos kysely ei ole SELECT.
    Task<string> ExecuteQueryAsync(string sql, CancellationToken cancellationToken = default);
}

// Suorittaa LLM:n generoimia Cosmos DB SQL -kyselyitä.
// Käyttää samaa CosmosRagOptions-konfiguraatiota kuin CosmosRagService (sama kanta/container).
public class CosmosQueryService : IQueryService
{
    private readonly CosmosClient _cosmosClient;
    private readonly CosmosRagOptions _options;
    private readonly ILogger<CosmosQueryService> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public CosmosQueryService(
        CosmosClient cosmosClient,
        IOptions<CosmosRagOptions> options,
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
        var results = new List<JsonElement>();

        using var feed = container.GetItemQueryIterator<JsonElement>(queryDef);

        while (feed.HasMoreResults)
        {
            var page = await feed.ReadNextAsync(cancellationToken);
            foreach (var item in page)
                results.Add(item);
        }

        _logger.LogInformation("Query returned {Count} rows", results.Count);

        return JsonSerializer.Serialize(results, JsonOpts);
    }
}
