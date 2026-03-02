# LlmGateway — Azure-infrastruktuuri

Käyttöönotto tyhjältä pohjalta, kronologisessa järjestyksessä.

---

## Vaihe 1 — Generoi salaisuudet

```bash
# Gateway API-avain (asiakkaat lähettävät X-Api-Key -headerissa)
openssl rand -hex 32

# MS SQL admin-salasana (vaatimus: iso+pieni kirjain + numero + erikoismerkki)
openssl rand -base64 24
```

Tallenna arvot muistiin — tarvitaan myöhemmissä vaiheissa.

---

## Vaihe 2 — Päivitä `infra/main.bicepparam`

Muuta globaalisti uniikit nimet (kaikki Azure-nimet, joissa `xyz`):

```
param appName            = 'xyz-llmgateway-prod'     // oltava globaalisti uniikki
param functionAppName    = 'xyz-sync-function-prod'  // oltava globaalisti uniikki
param openAIResourceName = 'xyz-openai-prod'          // oltava globaalisti uniikki
param cosmosAccountName  = 'xyz-cosmos-prod'          // oltava globaalisti uniikki
```

---

## Vaihe 3 — Luo Service Principal (GitHub Actions → Azure)

```bash
az ad sp create-for-rbac \
  --name llmgateway-deploy \
  --role Contributor \
  --scopes /subscriptions/<SUBSCRIPTION_ID>/resourceGroups/rg-llmgateway-prod \
  --json-auth
```

> Huom: resource group ei tarvitse olla olemassa tässä vaiheessa — `infra.yml` luo sen.

Tallenna komennon tulosteesta:
- `clientId` → `AZURE_CLIENT_ID`
- `tenantId` → `AZURE_TENANT_ID`
- `subscriptionId` → `AZURE_SUBSCRIPTION_ID`

OIDC-federated credential (korvaa `<APP_ID>` yllä luodun service principalin `clientId`:llä):

```bash
echo '{"name":"github-production","issuer":"https://token.actions.githubusercontent.com","subject":"repo:<GITHUB_ORG>/<REPO>:environment:production","audiences":["api://AzureADTokenExchange"]}' > fedcred.json
az ad app federated-credential create --id <APP_ID> --parameters @fedcred.json
```

---

## Vaihe 4 — Aseta GitHub Secrets ja Variables

**Repo → Settings → Secrets and variables → Actions**

| Tyyppi   | Nimi                           | Arvo                                              |
|----------|--------------------------------|---------------------------------------------------|
| Secret   | `AZURE_CLIENT_ID`              | Service principal clientId (vaihe 3)              |
| Secret   | `AZURE_TENANT_ID`              | Service principal tenantId (vaihe 3)              |
| Secret   | `AZURE_SUBSCRIPTION_ID`        | Azure subscription ID (vaihe 3)                   |
| Secret   | `GATEWAY_API_KEY`              | Generoitu vaiheessa 1                             |
| Secret   | `AZURE_SQL_ADMIN_PASSWORD`     | Generoitu vaiheessa 1                             |
| Variable | `AZURE_RESOURCE_GROUP`         | `rg-llmgateway-prod`                              |
| Variable | `AZURE_WEBAPP_NAME`            | `xyz-llmgateway-prod` (sama kuin main.bicepparam) |
| Variable | `AZURE_FUNCTION_APP_NAME`      | `xyz-sync-function-prod` (sama kuin main.bicepparam) |

> Azure OpenAI API-avain ja Cosmos DB connection string **luetaan automaattisesti** Bicepissä
> luoduista resursseista — erillisiä secretejä ei tarvita.

---

## Vaihe 5 — Provisioi Azure-infra (Bicep)

**GitHub → Actions → Provision Infrastructure → Run workflow → main**

Tämä ajaa `infra.yml`:n, joka:
1. Luo resource groupin `rg-llmgateway-prod`
2. Ajaa `main.bicep` → luo kaiken: App Service, Azure OpenAI + model deploymentit, Cosmos DB, SQL Server, SyncFunction (Storage Account + Consumption Plan + Function App), Log Analytics, App Insights

> Kesto: ~10–15 min (model deploymentit hitaita).

---

## Vaihe 6 — Hae Publish Profilet

Tarvitaan koodi-deploymentia varten.

**App Service:**
Azure Portal → App Service `xyz-llmgateway-prod` → Overview → **Get publish profile** → lataa tiedosto → kopioi koko sisältö

**Function App:**
Azure Portal → Function App `xyz-sync-function-prod` → Overview → **Get publish profile** → lataa tiedosto → kopioi koko sisältö

Lisää GitHubiin (Secrets):

| Secret                          | Arvo                          |
|---------------------------------|-------------------------------|
| `AZURE_WEBAPP_PUBLISH_PROFILE`  | App Service publish profile   |
| `AZURE_FUNCTION_PUBLISH_PROFILE`| Function App publish profile  |

---

## Vaihe 7 — Deploy koodi

```bash
git push origin main
```

Käynnistää automaattisesti `deploy.yml` (LlmGateway) ja `deploy-function.yml` (SyncFunction).

---

## Infra-tiedostot

| Tiedosto | Sisältö |
|----------|---------|
| `infra/main.bicep` | Kaikki: App Service, Azure OpenAI + deploymentit, Cosmos DB, SQL Server, SyncFunction, Log Analytics, App Insights |
| `infra/main.bicepparam` | Parametrit (ei salaisuuksia) |
| `.github/workflows/infra.yml` | Infra-deploy — ajetaan manuaalisesti |
| `.github/workflows/deploy.yml` | LlmGateway-deploy — automaattinen push:lla |
| `.github/workflows/deploy-function.yml` | SyncFunction-deploy — automaattinen SyncFunction/-muutoksilla |

---

## Infra-muutosten ajaminen jatkossa

```
GitHub → Actions → Provision Infrastructure → Run workflow
```

Bicep on idempotent — turvallista ajaa uudelleen milloin tahansa.
