// SyncFunction — Azure Functions -infrastruktuuri
// Provisioi Function App:n joka synkronoi Cosmos DB:n muutokset MS SQL:ään 15 min välein.
//
// Edellytys: main.bicep on ajettu — tämä viittaa siellä luotuun SQL Serveriin ja App Insightsiin.
//
// Deploy:
//   az deployment group create \
//     --resource-group <rg> \
//     --template-file infra/function.bicep \
//     --parameters infra/function.bicepparam \
//     --parameters cosmosConnectionString="<yhteysjono>" \
//     --parameters sqlAdminPassword="<salasana>"

@description('Globally unique name for the Function App. Used as hostname: <functionAppName>.azurewebsites.net')
param functionAppName string

@description('App name from main.bicep — used to reference the SQL Server and Application Insights created there.')
param appName string

@description('Azure region. Defaults to resource group location.')
param location string = resourceGroup().location

// ── Cosmos DB ────────────────────────────────────────────────────────────────

@secure()
@description('Cosmos DB primary connection string. Pass via pipeline secret — never store in params file.')
param cosmosConnectionString string

@description('Cosmos DB database name.')
param cosmosDatabaseName string = 'ragdb'

@description('Cosmos DB container name.')
param cosmosContainerName string = 'documents'

// ── MS SQL ───────────────────────────────────────────────────────────────────

@secure()
@description('MS SQL Server admin password. Same value as used in main.bicep. Pass via pipeline secret.')
param sqlAdminPassword string

@description('MS SQL Server admin login.')
param sqlAdminLogin string = 'sqladmin'

@description('MS SQL database name.')
param sqlDatabaseName string = 'llmgateway'

// ── Viittaukset main.bicep-resursseihin ──────────────────────────────────────

// Application Insights luotu main.bicep:ssä
resource existingAppInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: '${appName}-insights'
}

// SQL Server luotu main.bicep:ssä — tarvitaan FQDN connection stringiä varten
resource existingSqlServer 'Microsoft.Sql/servers@2023-08-01-preview' existing = {
  name: '${appName}-sql'
}

// ── Storage Account (Azure Functions vaatii) ─────────────────────────────────

// Nimestä poistetaan väliviivat ja otetaan enintään 22 merkkiä + "st" = max 24 merkkiä
// Storage account -nimi: vain pienet kirjaimet ja numerot, 3–24 merkkiä
var storageAccountName = take('${replace(toLower(functionAppName), '-', '')}fnst', 24)

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    allowBlobPublicAccess: false
    minimumTlsVersion: 'TLS1_2'
  }
}

// ── Consumption Plan (serverless, ~0 €/kk idle) ───────────────────────────────
// Windows-pohjainen: Azure rajoittaa Linux Consumption -plaanin käyttöä resurssigrupeissa
// joissa on muita App Service -resursseja. .NET isolated worker toimii kummallakin.

resource consumptionPlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: '${functionAppName}-plan'
  location: location
  sku: {
    name: 'Y1'
    tier: 'Dynamic'
  }
}

// ── Function App ─────────────────────────────────────────────────────────────

resource functionApp 'Microsoft.Web/sites@2023-12-01' = {
  name: functionAppName
  location: location
  kind: 'functionapp'
  properties: {
    serverFarmId: consumptionPlan.id
    httpsOnly: true
    siteConfig: {
      appSettings: [
        // Functions runtime
        { name: 'FUNCTIONS_EXTENSION_VERSION', value: '~4' }
        { name: 'FUNCTIONS_WORKER_RUNTIME',    value: 'dotnet-isolated' }

        // Storage — Functions runtime tarvitsee tämän sisäiseen käyttöön
        { name: 'AzureWebJobsStorage', value: 'DefaultEndpointsProtocol=https;AccountName=${storage.name};AccountKey=${storage.listKeys().keys[0].value};EndpointSuffix=core.windows.net' }

        // Cosmos DB (synkronoinnin lähde)
        { name: 'CosmosRag__ConnectionString', value: cosmosConnectionString }
        { name: 'CosmosRag__DatabaseName',     value: cosmosDatabaseName }
        { name: 'CosmosRag__ContainerName',    value: cosmosContainerName }

        // MS SQL (synkronoinnin kohde) — sama server kuin main.bicep:ssä
        { name: 'Sql__ConnectionString', value: 'Server=${existingSqlServer.properties.fullyQualifiedDomainName};Database=${sqlDatabaseName};User Id=${sqlAdminLogin};Password=${sqlAdminPassword};Encrypt=True;TrustServerCertificate=False;' }

        // Application Insights — sama instanssi kuin main.bicep:ssä
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: existingAppInsights.properties.ConnectionString }
      ]
    }
  }
}

// ── Outputs ───────────────────────────────────────────────────────────────────

output functionAppName string = functionApp.name
output functionAppUrl  string = 'https://${functionApp.properties.defaultHostName}'
