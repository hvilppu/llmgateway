# Säädokumentit MS SQL -backendille — Azure Blob Storage

## Miksi ei RAG?

| Kriteeri | Meidän tilanne | RAG tarvitaan kun |
|---|---|---|
| Dokumenttimäärä | ~10 kpl | Satoja tai tuhansia |
| Dokumenttityyppi | Word-tiedostot, tunnettu rakenne | Kirjavat, ennustamattomat |
| Haku | Nimen perusteella riittää | Pitää löytää relevantti ilman nimeä |
| Infrastruktuuri | Ei ylimääräistä | Vektoritietokanta hyväksytty |
| Kustannus | Minimaalinen | Ei rajoitusta |

**RAG** (vektorihaku, embedding-malli, `VECTOR`-sarake SQL:ssä) on oikea ratkaisu
kun dokumentteja on niin paljon ettei tiedetä etukäteen mikä on relevantti.
**10 dokumentille se on yliampuvaa** — yksinkertaisempi ratkaisu toimii paremmin ja on
helpompi ylläpitää.

---

## Ratkaisu: Azure Blob Storage + `search_documents`-työkalu

Dokumentit tallennetaan Azure Blob Storageen. LLM saa system promptissa listan
saatavilla olevista dokumenteista ja hakee haluamansa nimen perusteella.

```
System prompt:
  Käytettävissä olevat dokumentit:
  - talvi-2024.docx        — Talvikauden 2024 sääraportti
  - kesä-2024.docx         — Kesäkauden 2024 analyysi
  - vuosiraportti-2023.docx — Koko vuoden 2023 yhteenveto

Käyttäjä: "Mitä talviraportissa sanotaan helmikuusta?"

LLM → search_documents("talvi-2024.docx")
    ← [dokumentin koko teksti]

LLM → query_database("SELECT AVG(lampotila) FROM mittaukset WHERE ...")
    ← [numerot]

LLM → "Talviraportissa todetaan... Mittausdata vahvistaa: keskilämpötila oli -8.2°C"
```

---

## Muutokset järjestyksessä

### 1. `infra/main.bicep`

Lisätään Storage Account ja blob-container:

```bicep
@description('Azure Storage container name for weather documents.')
param documentsContainerName string = 'weather-documents'

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: '${replace(appName, '-', '')}store'   // storage name: vain kirjaimet/numerot, max 24
  location: location
  sku: { name: 'Standard_LRS' }               // ~2 €/kk 10 dokumentille
  kind: 'StorageV2'
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false
    minimumTlsVersion: 'TLS1_2'
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storageAccount
  name: 'default'
}

resource documentsContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: documentsContainerName
  properties: { publicAccess: 'None' }
}
```

App Servicen appSettings-lohkoon lisätään:
```bicep
{ name: 'BlobStorage__ConnectionString', value: 'DefaultEndpointsProtocol=https;AccountName=${storageAccount.name};AccountKey=${storageAccount.listKeys().keys[0].value};EndpointSuffix=core.windows.net' }
{ name: 'BlobStorage__ContainerName',   value: documentsContainerName }
```

SQL pysyy **Basic-tierissä** — nostoa ei tarvita.

---

### 2. `appsettings.json`

```json
"BlobStorage": {
  "ConnectionString": "UseDevelopmentStorage=true",
  "ContainerName": "weather-documents"
}
```

---

### 3. `Services/DocumentService.cs` _(uusi tiedosto)_

Namespace: `LlmGateway.Services`

```csharp
public interface IBlobDocumentService
{
    // Listaa saatavilla olevat dokumentit nimineen ja kuvauksineen.
    Task<IReadOnlyList<BlobDocumentInfo>> ListDocumentsAsync(CancellationToken ct = default);

    // Hakee dokumentin sisällön nimellä ja palauttaa tekstinä.
    Task<string> GetDocumentContentAsync(string filename, CancellationToken ct = default);
}

public record BlobDocumentInfo(string Filename, string Description);
```

**BlobDocumentOptions:**
```csharp
public class BlobDocumentOptions
{
    public string ConnectionString { get; set; } = "";
    public string ContainerName { get; set; } = "weather-documents";
}
```

**AzureBlobDocumentService:**
- Käyttää `Azure.Storage.Blobs` NuGet-pakettia
- `ListDocumentsAsync`: listaa blobit, lukee `description`-metatiedon blob-metadatasta
- `GetDocumentContentAsync`: lataa .docx stream, purkaa tekstin `DocumentFormat.OpenXml`:lla
- Dokumenttilista välimuistissa 60 min (sama `SemaphoreSlim` double-check -pattern kuin `SchemaService`)
- `GetDocumentContentAsync`: ei välimuistia — tiedosto haetaan aina tuoreena
- Virhe tuntemattomalla nimellä: palauttaa `"Dokumenttia ei löydy: {filename}"`

**Word-tekstin purku (`DocumentFormat.OpenXml`):**
```csharp
using var wordDoc = WordprocessingDocument.Open(stream, false);
var body = wordDoc.MainDocumentPart?.Document?.Body;
return body?.InnerText ?? string.Empty;
```

---

### 4. `Endpoints/ChatEndpoints.cs`

**a) Uusi tool-kuvaus:**
```csharp
private static readonly object SqlSearchDocumentsTool = new {
    type = "function",
    function = new {
        name = "search_documents",
        description = "Hakee säädokumentin sisällön tiedostonimen perusteella. " +
                      "Käytä kun tarvitaan laadullista tietoa tai analyysejä. " +
                      "Saatavilla olevat tiedostot näkyvät system promptissa.",
        parameters = new {
            type = "object",
            properties = new {
                filename = new { type = "string", description = "Dokumentin tiedostonimi, esim. talvi-2024.docx" }
            },
            required = new[] { "filename" }
        }
    }
};
```

**b) SqlTools laajennetaan:**
```csharp
private static readonly object[] SqlTools = [SqlQueryDatabaseTool, SqlSearchDocumentsTool];
```

**c) MapChatEndpoints injektoi `IBlobDocumentService`:**
```csharp
IBlobDocumentService documentService,
```

**d) `BuildSqlSystemPrompt` laajennetaan** ottamaan dokumenttilista parametrina:
```csharp
private static string BuildSqlSystemPrompt(string schema, string documentList) { ... }
```
Lisätään lohko:
```
Käytettävissä olevat dokumentit:
{documentList}

Käytä search_documents-työkalua laadulliseen tietoon ja query_database-työkalua
numeeriseen dataan.
```

**e) `HandleWithToolsAsync` hakee dokumenttilistan** ennen agenttilooppia:
```csharp
var docs = await documentService.ListDocumentsAsync(cancellationToken);
var documentList = string.Join("\n", docs.Select(d => $"- {d.Filename}  — {d.Description}"));
var systemPrompt = BuildSqlSystemPrompt(schema, documentList);
```

**f) `ExecuteToolAsync` saa uuden haaran:**
```csharp
"search_documents" => await documentService.GetDocumentContentAsync(
    args.GetProperty("filename").GetString()!, cancellationToken),
```

---

### 5. `Program.cs`

```csharp
// Blob Storage -dokumenttipalvelu (Word-tiedostot säädokumenteille)
builder.Services.Configure<BlobDocumentOptions>(
    builder.Configuration.GetSection("BlobStorage"));
builder.Services.AddSingleton<IBlobDocumentService, AzureBlobDocumentService>();
```

---

## Toteutusjärjestys

```
1. infra/main.bicep            — Storage Account + container (riippumaton, deploy erikseen)
2. appsettings.json            — BlobStorage-osio
3. Services/DocumentService.cs — IBlobDocumentService + toteutus
4. Endpoints/ChatEndpoints.cs  — tool + injektio + system prompt -laajennus
5. Program.cs                  — DI-rekisteröinti
```

**NuGet-paketit lisättävä:**
- `Azure.Storage.Blobs`
- `DocumentFormat.OpenXml`

---

## Testaus

```bash
# Lataa testidokumentti Blob Storageen ensin (Azure CLI):
az storage blob upload \
  --account-name <storageAccountName> \
  --container-name weather-documents \
  --name talvi-2024.docx \
  --file ./talvi-2024.docx \
  --metadata description="Talvikauden 2024 sääraportti"

# Dokumenttihaku — search_documents pitäisi aktivoitua
curl -X POST http://localhost:5079/api/chat \
  -H "Content-Type: application/json" -H "X-Api-Key: YOUR-KEY" \
  -d '{"message": "Mitä talviraportissa sanotaan helmikuusta?", "policy": "tools_sql"}'

# Hybriditesti — molemmat työkalut
curl -X POST http://localhost:5079/api/chat \
  -H "Content-Type: application/json" -H "X-Api-Key: YOUR-KEY" \
  -d '{"message": "Vertaa talviraportin havaintoja mittausdataan helmikuulta 2024", "policy": "tools_sql"}'
```

---

## Huomiot

- **Blob metadata `description`**: asetetaan dokumenttia ladattaessa, näkyy LLM:lle listassa
- **Cos pysyy Basic-tierissä**: Storage Account ei vaadi SQL-tason nostoa
- **Kustannus**: Standard_LRS Storage Account ~2 €/kk 10 pienelle Word-tiedostolle
- **Skaalaus myöhemmin**: jos dokumentteja tulee satoja, välimuistiton `ListDocumentsAsync`
  voi hidastua — silloin harkitaan RAG:ia tai SQL-taulua indeksoinnille
