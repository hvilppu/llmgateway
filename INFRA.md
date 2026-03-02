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

```bash
az deployment group create --resource-group rg-llmgateway-prod --template-file infra/function.bicep --parameters infra/function.bicepparam --parameters cosmosConnectionString="<yhteysjono>" --parameters sqlAdminPassword="<salasana>"
```

### 4b. Provisioi SyncFunction (Cosmos → SQL automaattisynkronointi)

SyncFunction pyörii Azure Functions Consumption-planilla ja synkronoi Cosmos DB:n muutokset
MS SQL:ään 15 minuutin välein. Se myös ajaa SQL-migraatiot (taulujen luonnit) automaattisesti
käynnistyksessä — manuaalista `seed_mssql.py` -ajoa ei tarvita.

Päivitä `infra/function.bicepparam`:
```
param functionAppName = 'xyz-sync-function-prod'   # oltava globaalisti uniikki
param appName         = 'xyz-llmgateway-prod'       # sama kuin main.bicepparam:ssä
```

Tarkista mitä muuttuu:
```bash
az deployment group what-if \
  --resource-group rg-llmgateway-prod \
  --template-file infra/function.bicep \
  --parameters infra/function.bicepparam \
  --parameters cosmosConnectionString="<AZURE_COSMOS_CONNECTION_STRING>" \
  --parameters sqlAdminPassword="<salasana>"
```

Provisioi Function App -infrastruktuuri:
```bash
az deployment group create \
  --resource-group rg-llmgateway-prod \
  --template-file infra/function.bicep \
  --parameters infra/function.bicepparam \
  --parameters cosmosConnectionString="<AZURE_COSMOS_CONNECTION_STRING>" \
  --parameters sqlAdminPassword="<salasana>"
```

Hae publish profile (tarvitaan GitHub Secretiin):
```
Azure Portal → Function App → Overview → Get publish profile → lataa tiedosto → kopioi sisältö
```

Aseta GitHub-asetukset (lisäys olemassaolevien secretien rinnalle):

| Tyyppi   | Nimi                              | Arvo                                                |
|----------|-----------------------------------|-----------------------------------------------------|
| Secret   | `AZURE_FUNCTION_PUBLISH_PROFILE`  | Function App → Get publish profile                  |
| Variable | `AZURE_FUNCTION_APP_NAME`         | `xyz-sync-function-prod`                            |

Deploy käynnistyy automaattisesti kun `SyncFunction/`-hakemistoon pushataan muutoksia
(tai manuaalisesti: Actions → **Deploy Function** → **Run workflow**).

> **Takaportti:** `tools/seed_mssql.py` on yhä käytettävissä manuaaliseen bulk-siirtoon
> esimerkiksi ensimmäisen käyttöönoton tai vianetsinnän yhteydessä.

### 5. Aseta GitHub Secrets ja Variables

| Tyyppi   | Nimi                              | Arvo                                                                  |
|----------|-----------------------------------|-----------------------------------------------------------------------|
| Secret   | `AZURE_WEBAPP_PUBLISH_PROFILE`    | Azure Portal → App Service → Get publish profile                      |
| Secret   | `AZURE_FUNCTION_PUBLISH_PROFILE`  | Azure Portal → Function App → Get publish profile                     |
| Secret   | `GATEWAY_API_KEY`                 | Itse generoitu — asiakkaat lähettävät `X-Api-Key` -headerissa         |
| Secret   | `AZURE_OPENAI_API_KEY`            | Azure Portal → Azure OpenAI → Keys and Endpoint → KEY 1               |
| Secret   | `AZURE_COSMOS_CONNECTION_STRING`  | Azure Portal → Cosmos DB → Keys → PRIMARY CONNECTION STRING           |
| Secret   | `AZURE_SQL_ADMIN_PASSWORD`        | MS SQL -ylläpitäjän salasana (sama kuin bicep-parametrissa)           |
| Secret   | `AZURE_CLIENT_ID`                 | Service principal OIDC (infra.yml)                                    |
| Secret   | `AZURE_TENANT_ID`                 | Service principal OIDC (infra.yml)                                    |
| Secret   | `AZURE_SUBSCRIPTION_ID`           | Service principal OIDC (infra.yml)                                    |
| Variable | `AZURE_WEBAPP_NAME`               | `xyz-llmgateway-prod`                                                 |
| Variable | `AZURE_FUNCTION_APP_NAME`         | `xyz-sync-function-prod`                                              |
| Variable | `AZURE_RESOURCE_GROUP`            | `rg-llmgateway-prod`                                                  |

### 6. Deploy

Push `main`-haaraan → `deploy.yml` käynnistyy automaattisesti.

Infra-muutokset: aja `infra.yml` manuaalisesti GitHub Actions → **Provision Infrastructure** → **Run workflow**.


echo {"name":"github-production","issuer":"https://token.actions.githubusercontent.com","subject":"repo:hvilppu/llmgateway:environment:production" audiences":["api://AzureADTokenExchange"]} > fedcred.json
az ad app federated-credential create --id <APP_ID> --parameters @fedcred.json

---

## Infra-tiedostot

| Tiedosto | Kuvaus |
|----------|--------|
| `infra/main.bicep` | App Service Plan, Web App, SQL Server, Log Analytics, App Insights |
| `infra/main.bicepparam` | Parametriarvot pääinfrastruktuurille (ei salaisuuksia) |
| `infra/function.bicep` | Storage Account, Consumption Plan, Function App |
| `infra/function.bicepparam` | Parametriarvot Function App:lle (ei salaisuuksia) |
| `.github/workflows/deploy.yml` | LlmGateway-koodi-deploy — käynnistyy automaattisesti push:lla |
| `.github/workflows/deploy-function.yml` | SyncFunction-deploy — käynnistyy kun SyncFunction/ muuttuu |
| `.github/workflows/infra.yml` | Infra-deploy — ajetaan manuaalisesti |


## CI/CD GitHub
CI/CD-workflowit ovat .github/workflows/-kansiossa:                                         
  
  deploy.yml — automaattinen deploy                                                           
  - Triggeröityy kun pushataan main-haaraan (tai manuaalisesti workflow_dispatch)               
  - Tekee: dotnet restore → build → test → publish → deploy Azure App Serviceen               
  - Vaatii GitHub-asetukset:                                                                  
    - Secret: AZURE_WEBAPP_PUBLISH_PROFILE
    - Variable: AZURE_WEBAPP_NAME

  deploy-function.yml — SyncFunction-deploy
  - Triggeröityy kun SyncFunction/-hakemistoon pushataan muutoksia (tai manuaalisesti workflow_dispatch)
  - Tekee: dotnet restore → build → publish → deploy Azure Functions -palveluun
  - Vaatii GitHub-asetukset:
    - Secret: AZURE_FUNCTION_PUBLISH_PROFILE
    - Variable: AZURE_FUNCTION_APP_NAME

  infra.yml — infrastruktuuri (Bicep)
  - Triggeröityy vain manuaalisesti (workflow_dispatch), ei commitista
  - Ajaa az deployment group create Bicep-templatella
  - Vaatii Secrets: AZURE_CLIENT_ID, AZURE_TENANT_ID, AZURE_SUBSCRIPTION_ID, GATEWAY_API_KEY,
  AZURE_OPENAI_API_KEY, AZURE_COSMOS_CONNECTION_STRING
  - Variable: AZURE_RESOURCE_GROUP

Eli commit main-haaraan laukaisee automaattisesti deploy.yml-buildin. Infra deployataan erikseen manuaalisesti GitHubista.

1. Mene repon GitHub-sivulle                                                                                                                          
2. Klikkaa 2.2.1 Actions-välilehti                                                                                                                                                 
3. Valitse vasemmalta listalta "Provision Infrastructure"                                                                                                                    
4. Klikkaa "Run workflow" -nappi (oikealla puolella)
5. Valitse haara (main) → "Run workflow"

