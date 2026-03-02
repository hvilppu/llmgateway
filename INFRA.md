# LlmGateway — Azure-infrastruktuuri

Bicep + GitHub Actions -pohjainen Azure App Service -deploymentin käyttöönotto.

---

## Käyttöönotto-järjestys

### 0. Resource group

```bash
az group create --name rg-llmgateway-prod --location swedencentral
```

### 1. Päivitä `infra/main.bicepparam`

Muuta globaalisti uniikit nimet omiksi:

```
param appName           = 'xyz-llmgateway-prod'     # oltava globaalisti uniikki
param openAIResourceName = 'xyz-openai-prod'         # oltava globaalisti uniikki
param cosmosAccountName  = 'xyz-cosmos-prod'         # oltava globaalisti uniikki
param sqlAdminLogin      = 'sqladmin'
param sqlDatabaseName    = 'llmgateway'
```

Seuraavat arvot tulevat pipeline-secreteistä — **ei koskaan params-tiedostoon**:
- `gatewayApiKey` ← `GATEWAY_API_KEY`
- `sqlAdminPassword` ← `AZURE_SQL_ADMIN_PASSWORD`

> Azure OpenAI API-avain ja Cosmos DB connection string **luetaan automaattisesti** Bicepissä
> luoduista resursseista (`listKeys()` / `listConnectionStrings()`). Erillisiä secretejä ei tarvita.

Mistä löydät:
- Gateway API-avain: generoi itse, esim. `openssl rand -hex 32`
- SQL admin-salasana: generoi itse, esim. `openssl rand -base64 24` (vaatimus: iso+pieni kirjain + numero + erikoismerkki)

### 2. Päivitä `infra/function.bicepparam`

```
param functionAppName   = 'xyz-sync-function-prod'  # oltava globaalisti uniikki
param appName           = 'xyz-llmgateway-prod'      # sama kuin main.bicepparam:ssä
param cosmosAccountName = 'xyz-cosmos-prod'          # sama kuin main.bicepparam:ssä
```

### 3. Provisioi infra

Tarkista ensin mitä muuttuu:
```bash
az deployment group what-if \
  --resource-group rg-llmgateway-prod \
  --template-file infra/main.bicep \
  --parameters infra/main.bicepparam \
  --parameters gatewayApiKey="<avain>" \
  --parameters sqlAdminPassword="<salasana>"
```

Aja infra (idempotent — turvallista ajaa uudelleen myös muutosten jälkeen):
```bash
az deployment group create \
  --resource-group rg-llmgateway-prod \
  --template-file infra/main.bicep \
  --parameters infra/main.bicepparam \
  --parameters gatewayApiKey="<avain>" \
  --parameters sqlAdminPassword="<salasana>"
```

```bash
az deployment group create \
  --resource-group rg-llmgateway-prod \
  --template-file infra/function.bicep \
  --parameters infra/function.bicepparam \
  --parameters sqlAdminPassword="<salasana>"
```

> Tai aja molemmat kerralla GitHub Actionsista: **Actions → Provision Infrastructure → Run workflow**

### 3b. SyncFunction-kuvaus

SyncFunction pyörii Azure Functions Consumption-planilla ja synkronoi Cosmos DB:n muutokset
MS SQL:ään 15 minuutin välein. Se myös ajaa SQL-migraatiot (taulujen luonnit) automaattisesti
käynnistyksessä — manuaalista `seed_mssql.py` -ajoa ei tarvita.

> **Takaportti:** `tools/seed_mssql.py` on yhä käytettävissä manuaaliseen bulk-siirtoon
> esimerkiksi ensimmäisen käyttöönoton tai vianetsinnän yhteydessä.

### 4. Aseta GitHub Secrets ja Variables

| Tyyppi   | Nimi                              | Arvo                                                                  |
|----------|-----------------------------------|-----------------------------------------------------------------------|
| Secret   | `AZURE_WEBAPP_PUBLISH_PROFILE`    | Azure Portal → App Service → Get publish profile                      |
| Secret   | `AZURE_FUNCTION_PUBLISH_PROFILE`  | Azure Portal → Function App → Get publish profile                     |
| Secret   | `GATEWAY_API_KEY`                 | Itse generoitu — asiakkaat lähettävät `X-Api-Key` -headerissa         |
| Secret   | `AZURE_SQL_ADMIN_PASSWORD`        | MS SQL -ylläpitäjän salasana (sama kuin bicep-parametrissa)           |
| Secret   | `AZURE_CLIENT_ID`                 | Service principal OIDC (infra.yml)                                    |
| Secret   | `AZURE_TENANT_ID`                 | Service principal OIDC (infra.yml)                                    |
| Secret   | `AZURE_SUBSCRIPTION_ID`           | Service principal OIDC (infra.yml)                                    |
| Variable | `AZURE_WEBAPP_NAME`               | `xyz-llmgateway-prod`                                                 |
| Variable | `AZURE_FUNCTION_APP_NAME`         | `xyz-sync-function-prod`                                              |
| Variable | `AZURE_RESOURCE_GROUP`            | `rg-llmgateway-prod`                                                  |

> `AZURE_OPENAI_API_KEY` ja `AZURE_COSMOS_CONNECTION_STRING` **poistettu** —
> Bicep lukee ne automaattisesti resursseista.

### 5. Deploy

Push `main`-haaraan → `deploy.yml` käynnistyy automaattisesti.

Infra-muutokset: aja `infra.yml` manuaalisesti GitHub Actions → **Provision Infrastructure** → **Run workflow**.


echo {"name":"github-production","issuer":"https://token.actions.githubusercontent.com","subject":"repo:hvilppu/llmgateway:environment:production" audiences":["api://AzureADTokenExchange"]} > fedcred.json
az ad app federated-credential create --id <APP_ID> --parameters @fedcred.json

---

## Infra-tiedostot

| Tiedosto | Kuvaus |
|----------|--------|
| `infra/main.bicep` | App Service Plan, Web App, Azure OpenAI, model deploymentit, Cosmos DB, SQL Server, Log Analytics, App Insights |
| `infra/main.bicepparam` | Parametriarvot pääinfrastruktuurille (ei salaisuuksia) |
| `infra/function.bicep` | Storage Account, Consumption Plan, Function App |
| `infra/function.bicepparam` | Parametriarvot Function App:lle (ei salaisuuksia) |
| `.github/workflows/deploy.yml` | LlmGateway-koodi-deploy — käynnistyy automaattisesti push:lla |
| `.github/workflows/deploy-function.yml` | SyncFunction-deploy — käynnistyy kun SyncFunction/ muuttuu |
| `.github/workflows/infra.yml` | Infra-deploy (main.bicep + function.bicep) — ajetaan manuaalisesti |


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
  - Ajaa main.bicep + function.bicep peräkkäin
  - Vaatii Secrets: AZURE_CLIENT_ID, AZURE_TENANT_ID, AZURE_SUBSCRIPTION_ID,
    GATEWAY_API_KEY, AZURE_SQL_ADMIN_PASSWORD
  - Variable: AZURE_RESOURCE_GROUP

Eli commit main-haaraan laukaisee automaattisesti deploy.yml-buildin. Infra deployataan erikseen manuaalisesti GitHubista.

1. Mene repon GitHub-sivulle
2. Klikkaa Actions-välilehti
3. Valitse vasemmalta listalta "Provision Infrastructure"
4. Klikkaa "Run workflow" -nappi (oikealla puolella)
5. Valitse haara (main) → "Run workflow"
