# LlmGateway

ASP.NET Core 10 minimal API gateway for Azure OpenAI, with retry, timeout and circuit breaker.

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
param appName                 = 'xyz-llmgateway-prod'   # oltava globaalisti uniikki
param azureOpenAIEndpoint     = 'https://my-openai-resource.openai.azure.com/'
param gpt4DeploymentName      = 'gpt4-deployment'
param gpt4oMiniDeploymentName = 'gpt4o-mini-deployment'
param embeddingDeploymentName = 'text-embedding-3-small'
param cosmosConnectionString  = 'AccountEndpoint=https://...'   # Cosmos DB primary key connection string
param cosmosDatabaseName      = 'mydb'
param cosmosContainerName     = 'documents'
```

Cosmos DB connection string löytyy: **Azure Portal → Cosmos DB -tili → Keys → PRIMARY CONNECTION STRING**

### 4. Provisioi infra

Tarkista ensin mitä muuttuu:
```bash
az deployment group what-if --resource-group rg-llmgateway-prod --template-file infra/main.bicep --parameters infra/main.bicepparam --parameters azureOpenAIApiKey="<avaimesi>"
```

Aja infra (idempotent — turvallista ajaa uudelleen myös muutosten jälkeen):
```bash
az deployment group create --resource-group rg-llmgateway-prod --template-file infra/main.bicep --parameters infra/main.bicepparam --parameters azureOpenAIApiKey="<avaimesi>"
```

API-avain löytyy: **Azure Portal → Azure OpenAI -resurssi → Keys and Endpoint → KEY 1**

### 5. Aseta GitHub Secrets ja Variables

| Tyyppi   | Nimi                           | Arvo                                                    |
|----------|--------------------------------|---------------------------------------------------------|
| Secret   | `AZURE_WEBAPP_PUBLISH_PROFILE` | Azure Portal → App Service → Get publish profile        |
| Secret   | `AZURE_OPENAI_API_KEY`         | Azure Portal → Azure OpenAI → Keys and Endpoint → KEY 1 |
| Secret   | `AZURE_COSMOS_CONNECTION_STRING` | Azure Portal → Cosmos DB → Keys → PRIMARY CONNECTION STRING |
| Secret   | `AZURE_CLIENT_ID`              | Service principal (infra.yml)                           |
| Secret   | `AZURE_TENANT_ID`              | Service principal (infra.yml)                           |
| Secret   | `AZURE_SUBSCRIPTION_ID`        | Service principal (infra.yml)                           |
| Variable | `AZURE_WEBAPP_NAME`            | `xyz-llmgateway-prod`                                       |
| Variable | `AZURE_RESOURCE_GROUP`         | `rg-llmgateway-prod`                                    |

### 6. Deploy

Push `main`-haaraan → `deploy.yml` käynnistyy automaattisesti.

---

## Testaus

```bash
curl -X POST https://llmgateway-prod.azurewebsites.net/api/chat -H "Content-Type: application/json" -d "{\"message\": \"Hei\"}"
```

```bash
curl -X POST https://llmgateway-prod.azurewebsites.net/api/chat -H "Content-Type: application/json" -d "{\"message\": \"Analysoi tämä\", \"policy\": \"critical\"}"
```

```bash
curl -X POST https://llmgateway-prod.azurewebsites.net/api/chat -H "Content-Type: application/json" -d "{\"message\": \"Mikä oli lämpötilan keskiarvo Helsingissä helmikuussa 2025?\", \"policy\": \"tools\"}"
```

```bash
curl -X POST https://llmgateway-prod.azurewebsites.net/api/chat -H "Content-Type: application/json" -d "{\"message\": \"Selitä miksi lämpötila vaihtelee vuodenajan mukaan\", \"policy\": \"tools\"}"
```
