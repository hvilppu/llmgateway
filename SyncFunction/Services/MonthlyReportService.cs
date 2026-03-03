using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SyncFunction.Services;

// Kuukausiraportin generointipalvelun asetukset. Sidotaan appsettings-osioon "MonthlyReport".
public class MonthlyReportOptions
{
    public string AzureOpenAIEndpoint { get; set; } = string.Empty;
    public string AzureOpenAIApiKey { get; set; } = string.Empty;
    public string ApiVersion { get; set; } = "2024-02-15-preview";
    public string CompletionDeploymentName { get; set; } = string.Empty;  // esim. gpt-4o-mini
    public string EmbeddingDeploymentName { get; set; } = string.Empty;   // esim. text-embedding-3-small
    public string ReportContainerName { get; set; } = "kuukausiraportit";
}

// Generoi kuukausittaiset laadulliset sääraportit Cosmos DB -kuukausiraportit-säiliöön.
// Hakee päivittäiset mittaukset documents-säiliöstä, pyytää GPT-4o-miniltä laadullisen
// kuvaustekstin (ei lukuja), laskee sille embedding-vektorin ja upsertaa raportticontaineriin.
public class MonthlyReportService
{
    private readonly CosmosClient _cosmosClient;
    private readonly MonthlyReportOptions _reportOptions;
    private readonly CosmosOptions _cosmosOptions;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<MonthlyReportService> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public MonthlyReportService(
        CosmosClient cosmosClient,
        IOptions<MonthlyReportOptions> reportOptions,
        IOptions<CosmosOptions> cosmosOptions,
        IHttpClientFactory httpClientFactory,
        ILogger<MonthlyReportService> logger)
    {
        _cosmosClient = cosmosClient;
        _reportOptions = reportOptions.Value;
        _cosmosOptions = cosmosOptions.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    // Generoi raportit kaikille (paikkakunta, vuosi, kuukausi) -yhdistelmille joille löytyy dataa.
    // Kutsutaan kerran käynnistyksessä ReportBackfillService:n kautta — täyttää historiadatan.
    public async Task GenerateAllMonthsReportsAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_reportOptions.AzureOpenAIEndpoint) ||
            string.IsNullOrWhiteSpace(_reportOptions.CompletionDeploymentName) ||
            string.IsNullOrWhiteSpace(_reportOptions.EmbeddingDeploymentName))
        {
            _logger.LogWarning("MonthlyReport-asetukset puutteelliset — historiadatan backfill ohitetaan.");
            return;
        }

        if (!await IsReportContainerReadyAsync(cancellationToken))
            return;

        _logger.LogInformation("Kaikkien kuukausiraporttien backfill alkaa.");

        // Haetaan kaikki mittaukset kerralla ja ryhmitellään muistissa
        var all = await FetchMeasurementsAsync(string.Empty, cancellationToken);

        if (all.Count == 0)
        {
            _logger.LogInformation("Ei mittauksia — backfill ohitetaan.");
            return;
        }

        // Ryhmittele paikkakunta + vuosi + kuukausi
        var groups = all
            .Where(m => m.Pvm.Length >= 7)
            .GroupBy(m => (
                Paikkakunta: m.Paikkakunta,
                Vuosi: int.Parse(m.Pvm[..4]),
                Kuukausi: int.Parse(m.Pvm[5..7])))
            .ToList();

        _logger.LogInformation("Backfill: {Count} kuukausi-paikkakunta-yhdistelmää.", groups.Count);

        foreach (var g in groups)
        {
            try
            {
                await GenerateCityMonthReportAsync(
                    g.Key.Paikkakunta, g.Key.Vuosi, g.Key.Kuukausi, g.ToList(), cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Backfill epäonnistui. Paikkakunta={City}, Vuosi={Year}, Kuukausi={Month}",
                    g.Key.Paikkakunta, g.Key.Vuosi, g.Key.Kuukausi);
            }
        }

        _logger.LogInformation("Kaikkien kuukausiraporttien backfill valmis.");
    }

    // Generoi tai päivittää kuukausiraportit kuluvalle kuukaudelle.
    // Kutsutaan CosmosToSqlTrigger-ajastimesta synkronoinnin jälkeen.
    public async Task GenerateCurrentMonthReportsAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_reportOptions.AzureOpenAIEndpoint) ||
            string.IsNullOrWhiteSpace(_reportOptions.CompletionDeploymentName) ||
            string.IsNullOrWhiteSpace(_reportOptions.EmbeddingDeploymentName))
        {
            _logger.LogWarning("MonthlyReport-asetukset puutteelliset — kuukausiraporttien generointi ohitetaan.");
            return;
        }

        if (!await IsReportContainerReadyAsync(cancellationToken))
            return;

        var now = DateTime.UtcNow;
        var datePrefix = $"{now.Year}-{now.Month:D2}";

        _logger.LogInformation("Kuukausiraporttien generointi alkaa. Kuukausi={DatePrefix}", datePrefix);

        var measurements = await FetchMeasurementsAsync(datePrefix, cancellationToken);

        if (measurements.Count == 0)
        {
            _logger.LogInformation("Ei mittauksia kuukaudelle {DatePrefix}", datePrefix);
            return;
        }

        // Ryhmittele paikkakunnittain
        var byCity = measurements
            .GroupBy(m => m.Paikkakunta)
            .ToDictionary(g => g.Key, g => g.ToList());

        _logger.LogInformation("Löydetty {CityCount} paikkakuntaa, yhteensä {MeasCount} mittausta",
            byCity.Count, measurements.Count);

        foreach (var (city, cityMeasurements) in byCity)
        {
            try
            {
                await GenerateCityMonthReportAsync(city, now.Year, now.Month, cityMeasurements, cancellationToken);
            }
            catch (Exception ex)
            {
                // Jatketaan muihin paikkakuntiin vaikka yksi epäonnistuu
                _logger.LogError(ex, "Kuukausiraportin generointi epäonnistui. Paikkakunta={City}", city);
            }
        }

        _logger.LogInformation("Kuukausiraporttien generointi valmis. Kuukausi={DatePrefix}", datePrefix);
    }

    // Tarkistaa että kuukausiraportit-container on olemassa ennen kirjoitusyritystä.
    // Container luodaan infran phase 2 -deploymentilla (deployVectorContainer=true).
    // Jos container puuttuu, palautetaan false ja operaatio ohitetaan hiljaisesti.
    private async Task<bool> IsReportContainerReadyAsync(CancellationToken ct)
    {
        var container = _cosmosClient
            .GetDatabase(_cosmosOptions.DatabaseName)
            .GetContainer(_reportOptions.ReportContainerName);
        try
        {
            await container.ReadContainerAsync(cancellationToken: ct);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogWarning(
                "Kuukausiraportit-container '{Container}' ei ole vielä olemassa — ohitetaan. " +
                "Aja infra uudelleen parametrilla deployVectorContainer=true.",
                _reportOptions.ReportContainerName);
            return false;
        }
    }

    // Hakee päivittäiset mittaukset. Tyhjä datePrefix = kaikki dokumentit (backfill).
    // Tukee molempia Cosmos-dokumenttirakenteita: content-aliobjekti ja ylätason kentät.
    private async Task<List<Measurement>> FetchMeasurementsAsync(string datePrefix, CancellationToken ct)
    {
        var container = _cosmosClient
            .GetDatabase(_cosmosOptions.DatabaseName)
            .GetContainer(_cosmosOptions.ContainerName);

        var sql = string.IsNullOrEmpty(datePrefix)
            ? "SELECT * FROM c"
            : "SELECT * FROM c WHERE STARTSWITH(c.content.pvm, @prefix) OR STARTSWITH(c.pvm, @prefix)";

        var qd = new QueryDefinition(sql);
        if (!string.IsNullOrEmpty(datePrefix))
            qd = qd.WithParameter("@prefix", datePrefix);

        var query = qd;

        var results = new List<Measurement>();
        using var feed = container.GetItemQueryStreamIterator(query);

        while (feed.HasMoreResults)
        {
            using var response = await feed.ReadNextAsync(ct);
            using var doc = await JsonDocument.ParseAsync(response.Content, cancellationToken: ct);

            if (!doc.RootElement.TryGetProperty("Documents", out var documents))
                continue;

            foreach (var item in documents.EnumerateArray())
            {
                var m = ParseMeasurement(item);
                if (m is not null)
                    results.Add(m);
            }
        }

        return results;
    }

    // Generoi yhdelle paikkakunnalle kuukausiraportin ja upsertaa Cosmos DB:hen.
    private async Task GenerateCityMonthReportAsync(
        string city, int year, int month,
        List<Measurement> measurements,
        CancellationToken ct)
    {
        // Päivittäiset lämpötilarivit LLM-promptia varten (numerot OK promptissa, ei vastauksessa)
        var dataLines = measurements
            .OrderBy(m => m.Pvm)
            .Select(m => $"- {m.Pvm}: {m.Lampotila:F1} °C");

        var dataText = string.Join("\n", dataLines);

        var kuvaus = await GenerateKuvausAsync(city, year, month, dataText, ct);
        var embedding = await GetEmbeddingAsync(kuvaus, ct);

        var reportId = $"{city}-{year}-{month}";
        var report = new
        {
            id = reportId,
            paikkakunta = city,
            vuosi = year,
            kuukausi = month,
            kuvaus,
            embedding
        };

        var raportitContainer = _cosmosClient
            .GetDatabase(_cosmosOptions.DatabaseName)
            .GetContainer(_reportOptions.ReportContainerName);

        await raportitContainer.UpsertItemAsync(report, new PartitionKey(reportId), cancellationToken: ct);

        _logger.LogInformation("Kuukausiraportti tallennettu. Id={ReportId}", reportId);
    }

    // Kutsuu GPT-4o-miniä laadullisen kuvaustekstin generoimiseksi.
    // Ohje: ei lukuja, tilastoja tai asteita — pelkkä laadullinen kuvaus.
    private async Task<string> GenerateKuvausAsync(
        string city, int year, int month, string dataText, CancellationToken ct)
    {
        var monthName = new System.Globalization.CultureInfo("fi-FI").DateTimeFormat.GetMonthName(month);

        var messages = new object[]
        {
            new
            {
                role = "system",
                content = "Kirjoita suomenkielinen, lyhyt (2-3 lausetta) laadullinen kuvausteksti " +
                          "kuukauden säästä. Älä mainitse lukuja, tilastoja tai asteita — kuvaile " +
                          "sanoin millainen sää oli (esim. kylmä, leuto, vaihteleva, talvinen, sateinen). " +
                          "Vastaa VAIN kuvaustekstillä, ei otsikoita eikä selityksiä."
            },
            new
            {
                role = "user",
                content = $"Paikkakunta: {city}\nKuukausi: {monthName} {year}\n\n" +
                          $"Päivittäiset lämpötilamittaukset:\n{dataText}"
            }
        };

        var url = $"/openai/deployments/{_reportOptions.CompletionDeploymentName}/chat/completions" +
                  $"?api-version={_reportOptions.ApiVersion}";

        var payload = new { messages, temperature = 0.7, max_tokens = 200 };
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await SendAsync(url, content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Kuvaus-kutsu epäonnistui: {(int)response.StatusCode} — {body[..Math.Min(300, body.Length)]}");

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? string.Empty;
    }

    // Laskee tekstin embedding-vektorin text-embedding-3-small -mallilla.
    private async Task<float[]> GetEmbeddingAsync(string text, CancellationToken ct)
    {
        var url = $"/openai/deployments/{_reportOptions.EmbeddingDeploymentName}/embeddings" +
                  $"?api-version={_reportOptions.ApiVersion}";

        var payload = new { input = text };
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await SendAsync(url, content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Embedding-kutsu epäonnistui: {(int)response.StatusCode}");

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement
            .GetProperty("data")[0]
            .GetProperty("embedding")
            .EnumerateArray()
            .Select(e => e.GetSingle())
            .ToArray();
    }

    // Luo HttpClientin IHttpClientFactory:n kautta ja asettaa Azure OpenAI -headerit.
    private async Task<HttpResponseMessage> SendAsync(string url, HttpContent content, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(_reportOptions.AzureOpenAIEndpoint);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _reportOptions.AzureOpenAIApiKey);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(30_000);

        return await client.PostAsync(url, content, cts.Token);
    }

    // Parsii mittauksen JSON-elementistä. Tukee content-aliobjektia ja ylätason kenttiä.
    private static Measurement? ParseMeasurement(JsonElement element)
    {
        string? paikkakunta = null;
        string? pvm = null;
        double? lampotila = null;

        if (element.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object)
        {
            paikkakunta = TryGetString(content, "paikkakunta");
            pvm = TryGetString(content, "pvm");
            lampotila = TryGetDouble(content, "lampotila") ?? TryGetDouble(content, "lämpötila");
        }

        paikkakunta ??= TryGetString(element, "paikkakunta");
        pvm ??= TryGetString(element, "pvm");
        lampotila ??= TryGetDouble(element, "lampotila") ?? TryGetDouble(element, "lämpötila");

        if (paikkakunta is null || pvm is null || lampotila is null)
            return null;

        return new Measurement(paikkakunta, pvm, lampotila.Value);
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

    // Yksittäisen päivämittauksen sisäinen esitys.
    private record Measurement(string Paikkakunta, string Pvm, double Lampotila);
}

// Ajaa historiadatan kuukausiraporttien generoinnin kerran käynnistyksen yhteydessä.
// Tämän jälkeen CosmosToSqlTrigger pitää kuluvan kuukauden ajan tasalla 15 min välein.
public class ReportBackfillService : IHostedService
{
    private readonly MonthlyReportService _reportService;
    private readonly ILogger<ReportBackfillService> _logger;

    public ReportBackfillService(
        MonthlyReportService reportService,
        ILogger<ReportBackfillService> logger)
    {
        _reportService = reportService;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("ReportBackfillService käynnistyy — generoidaan puuttuvat kuukausiraportit.");
        await _reportService.GenerateAllMonthsReportsAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
