using LlmGateway.Infrastructure;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;

namespace LlmGateway.Services;

// Konfiguraatio Cosmos DB -yhteyttä ja vektorihaun asetuksia varten.
// Sidotaan appsettings-osioon "CosmosRag".
public class CosmosRagOptions
{
    public string ConnectionString { get; set; } = string.Empty;
    public string DatabaseName { get; set; } = string.Empty;
    public string ContainerName { get; set; } = string.Empty;
    public int TopK { get; set; } = 5;               // Montako dokumenttia haetaan
    public string VectorField { get; set; } = "embedding";  // Kentän nimi Cosmos-dokumentissa
    public string ContentField { get; set; } = "content";   // Kentän nimi joka palautetaan kontekstiksi
}

// RAG-palvelun rajapinta. Mahdollistaa mock-toteutuksen testeissä.
public interface IRagService
{
    // Hakee Cosmos DB:stä query-tekstiä lähinnä olevat dokumentit ja
    // palauttaa niiden sisällöt yhdistettynä kontekstimerkkijonona.
    Task<string> RetrieveContextAsync(string query, CancellationToken cancellationToken = default);
}

// Hakee semanttisesti relevantteja dokumentteja Cosmos DB NoSQL -vektorihaulla.
// Käyttää Azure OpenAI Embeddings APIa query-vektorin generointiin.
public class CosmosRagService : IRagService
{
    private readonly CosmosClient _cosmosClient;
    private readonly IAzureOpenAIClient _embeddingClient;
    private readonly CosmosRagOptions _options;
    private readonly ILogger<CosmosRagService> _logger;

    public CosmosRagService(
        CosmosClient cosmosClient,
        IAzureOpenAIClient embeddingClient,
        IOptions<CosmosRagOptions> options,
        ILogger<CosmosRagService> logger)
    {
        _cosmosClient = cosmosClient;
        _embeddingClient = embeddingClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string> RetrieveContextAsync(string query, CancellationToken cancellationToken = default)
    {
        // 1. Generoi embedding query-tekstistä
        var embedding = await _embeddingClient.GetEmbeddingAsync(query, cancellationToken);

        _logger.LogInformation("Generated query embedding. Dimensions={Dimensions}", embedding.Length);

        // 2. Hae TOP K lähintä dokumenttia Cosmos DB:stä VectorDistance-haulla
        var container = _cosmosClient
            .GetDatabase(_options.DatabaseName)
            .GetContainer(_options.ContainerName);

        // Serialisoi float[] JSON-arrayksi suoraan queryyn (Cosmos DB ei tue parametreja VectorDistance:ssa)
        var embeddingJson = JsonSerializer.Serialize(embedding);

        var queryText = $"""
            SELECT TOP {_options.TopK} c.{_options.ContentField}
            FROM c
            ORDER BY VectorDistance(c.{_options.VectorField}, {embeddingJson})
            """;

        var queryDef = new QueryDefinition(queryText);
        var results = new List<string>();

        using var feed = container.GetItemQueryIterator<JsonElement>(queryDef);

        while (feed.HasMoreResults)
        {
            var page = await feed.ReadNextAsync(cancellationToken);
            foreach (var item in page)
            {
                if (item.TryGetProperty(_options.ContentField, out var contentEl))
                {
                    // Content voi olla merkkijono tai sisäkkäinen objekti (esim. { paikkakunta, pvm, lämpötila })
                    var text = contentEl.ValueKind == JsonValueKind.String
                        ? contentEl.GetString()
                        : contentEl.GetRawText();
                    if (!string.IsNullOrWhiteSpace(text))
                        results.Add(text);
                }
            }
        }

        _logger.LogInformation("RAG retrieved {Count} documents from Cosmos DB", results.Count);

        if (results.Count == 0)
            return string.Empty;

        // 3. Yhdistä dokumentit kontekstimerkkijonoksi
        var sb = new StringBuilder();
        for (int i = 0; i < results.Count; i++)
        {
            sb.AppendLine($"[{i + 1}] {results[i]}");
        }

        return sb.ToString().TrimEnd();
    }
}
