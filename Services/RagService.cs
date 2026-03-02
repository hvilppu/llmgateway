using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace LlmGateway.Services;

// RAG-palvelun asetukset. Sidotaan appsettings-osioon "Rag".
public class RagOptions
{
    public string DatabaseName { get; set; } = string.Empty;
    public string RaportitContainerName { get; set; } = "kuukausiraportit";
    public int TopK { get; set; } = 3;
}

// Hakee semanttisesti relevantit kuukausikuvaukset vektorihaulla.
public interface IRagService
{
    // Palauttaa top-K relevantit kuvaukset muotoiltuna kontekstitekstinä.
    Task<string> GetContextAsync(float[] queryEmbedding, CancellationToken cancellationToken = default);
}

// Cosmos DB -pohjainen RAG-haku. Käyttää VectorDistance-funktiota lähimpien dokumenttien löytämiseen.
public class CosmosRagService : IRagService
{
    private readonly CosmosClient _cosmosClient;
    private readonly RagOptions _options;
    private readonly ILogger<CosmosRagService> _logger;

    public CosmosRagService(
        CosmosClient cosmosClient,
        IOptions<RagOptions> options,
        ILogger<CosmosRagService> logger)
    {
        _cosmosClient = cosmosClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string> GetContextAsync(float[] queryEmbedding, CancellationToken cancellationToken = default)
    {
        var container = _cosmosClient
            .GetDatabase(_options.DatabaseName)
            .GetContainer(_options.RaportitContainerName);

        // VectorDistance palauttaa pienimmän arvon lähimmälle — ORDER BY nousevaan järjestykseen
        var query = new QueryDefinition(
            $"SELECT TOP {_options.TopK} c.paikkakunta, c.vuosi, c.kuukausi, c.kuvaus " +
            "FROM c ORDER BY VectorDistance(c.embedding, @queryVector)")
            .WithParameter("@queryVector", queryEmbedding);

        var results = new List<string>();

        using var feed = container.GetItemQueryStreamIterator(query);

        while (feed.HasMoreResults)
        {
            using var response = await feed.ReadNextAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(response.Content, cancellationToken: cancellationToken);

            if (!doc.RootElement.TryGetProperty("Documents", out var documents))
                continue;

            foreach (var item in documents.EnumerateArray())
            {
                var paikkakunta = item.TryGetProperty("paikkakunta", out var p) ? p.GetString() : "";
                var vuosi = item.TryGetProperty("vuosi", out var v) ? v.GetInt32() : 0;
                var kuukausi = item.TryGetProperty("kuukausi", out var k) ? k.GetInt32() : 0;
                var kuvaus = item.TryGetProperty("kuvaus", out var ku) ? ku.GetString() : "";

                results.Add($"{paikkakunta} {kuukausi}/{vuosi}: {kuvaus}");
            }
        }

        _logger.LogInformation("RAG-haku palautti {Count} dokumenttia", results.Count);

        return string.Join("\n\n", results);
    }
}
