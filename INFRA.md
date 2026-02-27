# LlmGateway — Azure-infrastruktuuri

Bicep + GitHub Actions -pohjainen Azure App Service -deploymentin käyttöönotto.

---

## Käyttöönotto-järjestys

### 0. Resource group

```bash
az group create --name rg-llmgateway-prod --location swedencentral
```

### 1. Luo Azure OpenAI -resurssi

```bash
az cognitiveservices account create --name my-openai-resource --resource-group rg-llmgateway-prod --kind OpenAI --sku S0 --location swedencentral
```

### 2. Luo model deploymentit

```bash
az cognitiveservices account deployment create --name my-openai-resource --resource-group rg-llmgateway-prod --deployment-name gpt4-deployment --model-name gpt-4o --model-version "2024-11-20" --model-format OpenAI --sku-capacity 10 --sku-name Standard
```

```bash
az cognitiveservices account deployment create --name my-openai-resource --resource-group rg-llmgateway-prod --deployment-name gpt4o-mini-deployment --model-name gpt-4o-mini --model-version "2024-07-18" --model-format OpenAI --sku-capacity 20 --sku-name Standard
```

```bash
az cognitiveservices account deployment create --name my-openai-resource --resource-group rg-llmgateway-prod --deployment-name text-embedding-3-small --model-name text-embedding-3-small --model-version "1" --model-format OpenAI --sku-capacity 10 --sku-name Standard
```

### 3. Päivitä `infra/main.bicepparam`

```
param appName                 = 'xyz-llmgateway-prod'           # oltava globaalisti uniikki
param azureOpenAIEndpoint     = 'https://my-openai-resource.openai.azure.com/'
param gpt4DeploymentName      = 'gpt4-deployment'
param gpt4oMiniDeploymentName = 'gpt4o-mini-deployment'
param embeddingDeploymentName = 'text-embedding-3-small'
param cosmosDatabaseName      = 'mydb'
param cosmosContainerName     = 'documents'
```

Seuraavat arvot tulevat pipeline-secreteistä — **ei koskaan params-tiedostoon**:
- `gatewayApiKey` ← `GATEWAY_API_KEY`
- `azureOpenAIApiKey` ← `AZURE_OPENAI_API_KEY`
- `cosmosConnectionString` ← `AZURE_COSMOS_CONNECTION_STRING`

Mistä löydät:
- OpenAI API-avain: **Azure Portal → Azure OpenAI -resurssi → Keys and Endpoint → KEY 1**
- Cosmos DB connection string: **Azure Portal → Cosmos DB -tili → Keys → PRIMARY CONNECTION STRING**
- Gateway API-avain: generoi itse, esim. `openssl rand -hex 32`

### 4. Provisioi infra

Tarkista ensin mitä muuttuu:
```bash
az deployment group what-if --resource-group rg-llmgateway-prod --template-file infra/main.bicep --parameters infra/main.bicepparam --parameters gatewayApiKey="<avain>" --parameters azureOpenAIApiKey="<avain>" --parameters cosmosConnectionString="<yhteysjono>"
```

Aja infra (idempotent — turvallista ajaa uudelleen myös muutosten jälkeen):
```bash
az deployment group create --resource-group rg-llmgateway-prod --template-file infra/main.bicep --parameters infra/main.bicepparam --parameters gatewayApiKey="<avain>" --parameters azureOpenAIApiKey="<avain>" --parameters cosmosConnectionString="<yhteysjono>"
```

### 5. Aseta GitHub Secrets ja Variables

| Tyyppi   | Nimi                             | Arvo                                                                  |
|----------|----------------------------------|-----------------------------------------------------------------------|
| Secret   | `AZURE_WEBAPP_PUBLISH_PROFILE`   | Azure Portal → App Service → Get publish profile                      |
| Secret   | `GATEWAY_API_KEY`                | Itse generoitu — asiakkaat lähettävät `X-Api-Key` -headerissa         |
| Secret   | `AZURE_OPENAI_API_KEY`           | Azure Portal → Azure OpenAI → Keys and Endpoint → KEY 1               |
| Secret   | `AZURE_COSMOS_CONNECTION_STRING` | Azure Portal → Cosmos DB → Keys → PRIMARY CONNECTION STRING           |
| Secret   | `AZURE_CLIENT_ID`                | Service principal OIDC (infra.yml)                                    |
| Secret   | `AZURE_TENANT_ID`                | Service principal OIDC (infra.yml)                                    |
| Secret   | `AZURE_SUBSCRIPTION_ID`          | Service principal OIDC (infra.yml)                                    |
| Variable | `AZURE_WEBAPP_NAME`              | `xyz-llmgateway-prod`                                                 |
| Variable | `AZURE_RESOURCE_GROUP`           | `rg-llmgateway-prod`                                                  |

### 6. Deploy

Push `main`-haaraan → `deploy.yml` käynnistyy automaattisesti.

Infra-muutokset: aja `infra.yml` manuaalisesti GitHub Actions → **Provision Infrastructure** → **Run workflow**.

---

## Infra-tiedostot

| Tiedosto | Kuvaus |
|----------|--------|
| `infra/main.bicep` | App Service Plan, Web App, Log Analytics, App Insights |
| `infra/main.bicepparam` | Parametriarvot (ei salaisuuksia) |
| `.github/workflows/deploy.yml` | Koodi-deploy — käynnistyy automaattisesti push:lla |
| `.github/workflows/infra.yml` | Infra-deploy — ajetaan manuaalisesti |

## Embedding-mallin versio

Tarkista mitä malleja Azure OpenAI -resurssissasi on saatavilla:

```bash
az cognitiveservices account list-models --name my-openai-resource --resource-group rg-llmgateway-prod --query "[?contains(name, 'embedding')].{name:name, version:version}" --output table
```

Sweden Centralissa yleensä toimivat:

| Malli | Versio | Huomio |
|-------|--------|--------|
| `text-embedding-3-small` | `1` | Suositeltu — pienin ja halvin |
| `text-embedding-3-large` | `1` | Tarkempi, kalliimpi |
| `text-embedding-ada-002` | `2` | Vanhempi, laajasti saatavilla |

Kun tiedät oikean mallin ja version, päivitä `infra/main.bicepparam`:

```
param embeddingDeploymentName = 'text-embedding-3-small'   # deployment-nimi jonka annoit
```

ja `appsettings.json`:

```json
"AzureOpenAI": {
  "EmbeddingDeployment": "text-embedding-3-small"
}
```
