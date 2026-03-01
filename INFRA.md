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

### 3. Luo Cosmos DB (Text-to-NoSQL)

Luo tili:
```bash
az cosmosdb create --name my-cosmos-account --resource-group rg-llmgateway-prod --kind GlobalDocumentDB --locations regionName=swedencentral
```

Luo tietokanta:
```bash
az cosmosdb sql database create --account-name my-cosmos-account --resource-group rg-llmgateway-prod --name ragdb
```

Luo container:
```bash
az cosmosdb sql container create --account-name my-cosmos-account --resource-group rg-llmgateway-prod --database-name ragdb --name documents --partition-key-path "/id"
```

Hae connection string (tarvitaan bicepparam + pipeline secret):
```bash
az cosmosdb keys list --name my-cosmos-account --resource-group rg-llmgateway-prod --type connection-strings --query "connectionStrings[0].connectionString" --output tsv
```

### 4. Päivitä `infra/main.bicepparam`

```
param appName                 = 'xyz-llmgateway-prod'           # oltava globaalisti uniikki
param azureOpenAIEndpoint     = 'https://my-openai-resource.openai.azure.com/'
param gpt4DeploymentName      = 'gpt4-deployment'
param gpt4oMiniDeploymentName = 'gpt4o-mini-deployment'
param cosmosDatabaseName      = 'mydb'
param cosmosContainerName     = 'documents'
param sqlAdminLogin           = 'sqladmin'                      # MS SQL -ylläpitäjän käyttäjänimi
param sqlDatabaseName         = 'llmgateway'                    # MS SQL -tietokannan nimi
```

Seuraavat arvot tulevat pipeline-secreteistä — **ei koskaan params-tiedostoon**:
- `gatewayApiKey` ← `GATEWAY_API_KEY`
- `azureOpenAIApiKey` ← `AZURE_OPENAI_API_KEY`
- `cosmosConnectionString` ← `AZURE_COSMOS_CONNECTION_STRING`
- `sqlAdminPassword` ← `AZURE_SQL_ADMIN_PASSWORD`

Mistä löydät:
- OpenAI API-avain: **Azure Portal → Azure OpenAI -resurssi → Keys and Endpoint → KEY 1**
- Cosmos DB connection string: **Azure Portal → Cosmos DB -tili → Keys → PRIMARY CONNECTION STRING**
- Gateway API-avain: generoi itse, esim. `openssl rand -hex 32`
- SQL admin-salasana: generoi itse, esim. `openssl rand -base64 24` (vaatimus: iso+pieni kirjain + numero + erikoismerkki)

### 4. Provisioi infra

Tarkista ensin mitä muuttuu:
```bash
az deployment group what-if --resource-group rg-llmgateway-prod --template-file infra/main.bicep --parameters infra/main.bicepparam --parameters gatewayApiKey="<avain>" --parameters azureOpenAIApiKey="<avain>" --parameters cosmosConnectionString="<yhteysjono>" --parameters sqlAdminPassword="<salasana>"
```

Aja infra (idempotent — turvallista ajaa uudelleen myös muutosten jälkeen):
```bash
az deployment group create --resource-group rg-llmgateway-prod --template-file infra/main.bicep --parameters infra/main.bicepparam --parameters gatewayApiKey="<avain>" --parameters azureOpenAIApiKey="<avain>" --parameters cosmosConnectionString="<yhteysjono>" --parameters sqlAdminPassword="<salasana>"
```

### 4b. Migroi data Cosmos DB:stä MS SQL:ään

Suorita kerran infra-provisionoinnin jälkeen. Hae SQL Serverin FQDN:

```bash
az sql server show --name <appName>-sql --resource-group rg-llmgateway-prod --query fullyQualifiedDomainName --output tsv
```

Aja migraatio:
```bash
pip install azure-cosmos pyodbc
python tools/seed_mssql.py \
  --cosmos-connection-string "<AZURE_COSMOS_CONNECTION_STRING>" \
  --cosmos-database mydb \
  --cosmos-container documents \
  --mssql-connection-string "Driver={ODBC Driver 17 for SQL Server};Server=<fqdn>;Database=llmgateway;UID=sqladmin;PWD=<salasana>;Encrypt=yes;TrustServerCertificate=no;"
```

Skripti on idempotentti (MERGE) — turvallista ajaa uudelleen.

### 5. Aseta GitHub Secrets ja Variables

| Tyyppi   | Nimi                             | Arvo                                                                  |
|----------|----------------------------------|-----------------------------------------------------------------------|
| Secret   | `AZURE_WEBAPP_PUBLISH_PROFILE`   | Azure Portal → App Service → Get publish profile                      |
| Secret   | `GATEWAY_API_KEY`                | Itse generoitu — asiakkaat lähettävät `X-Api-Key` -headerissa         |
| Secret   | `AZURE_OPENAI_API_KEY`           | Azure Portal → Azure OpenAI → Keys and Endpoint → KEY 1               |
| Secret   | `AZURE_COSMOS_CONNECTION_STRING` | Azure Portal → Cosmos DB → Keys → PRIMARY CONNECTION STRING           |
| Secret   | `AZURE_SQL_ADMIN_PASSWORD`       | MS SQL -ylläpitäjän salasana (sama kuin bicep-parametrissa)           |
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

