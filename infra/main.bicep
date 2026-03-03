// LlmGateway — Azure-infrastruktuuri
// Luo kaiken: App Service, Azure OpenAI, Cosmos DB, SQL Server, SyncFunction, Log Analytics, App Insights.
// Deploy: az deployment group create --resource-group <rg> --template-file infra/main.bicep --parameters infra/main.bicepparam

// ── Yleiset ───────────────────────────────────────────────────────────────────

@description('Globally unique name for the App Service. Used as the hostname: <appName>.azurewebsites.net')
param appName string

@description('Globally unique name for the Function App. Used as hostname: <functionAppName>.azurewebsites.net')
param functionAppName string

@description('Azure region. Defaults to resource group location.')
param location string = resourceGroup().location

@description('App Service Plan SKU. B1 = ~13 €/kk, P1v3 = ~70 €/kk.')
@allowed(['F1', 'B1', 'B2', 'P1v3', 'P2v3'])
param sku string = 'B1'

// ── API Key (Gateway) ─────────────────────────────────────────────────────────

@secure()
@description('Gateway API key. Clients must send this in X-Api-Key header. Pass via --parameters or pipeline secret.')
param gatewayApiKey string

// ── Azure OpenAI ──────────────────────────────────────────────────────────────

@description('Globally unique name for the Azure OpenAI Cognitive Services resource.')
param openAIResourceName string

@description('Azure OpenAI API version.')
param azureOpenAIApiVersion string = '2024-02-15-preview'

param azureOpenAITimeoutMs int = 15000
param azureOpenAIMaxRetries int = 2
param azureOpenAIRetryDelayMs int = 500

@description('Deployment name for gpt-4o (käytetään avaimena "gpt4").')
param gpt4DeploymentName string = 'gpt4-deployment'

@description('Deployment name for gpt-4o-mini (käytetään avaimena "gpt4oMini").')
param gpt4oMiniDeploymentName string = 'gpt4o-mini-deployment'

@description('gpt-4o -mallin versio.')
param gpt4ModelVersion string = '2024-11-20'

@description('gpt-4o-mini -mallin versio.')
param gpt4oMiniModelVersion string = '2024-07-18'

@description('gpt-4o -deploymentin TPM-kapasiteetti (tuhansina).')
param gpt4Capacity int = 10

@description('gpt-4o-mini -deploymentin TPM-kapasiteetti (tuhansina).')
param gpt4oMiniCapacity int = 20

@description('Deployment name for text-embedding-3-small (käytetään RAG-embeddaukseen).')
param embeddingDeploymentName string = 'embedding-deployment'

@description('text-embedding-3-small -mallin versio.')
param embeddingModelVersion string = '1'

@description('Embedding-deploymentin TPM-kapasiteetti (tuhansina).')
param embeddingCapacity int = 10

// ── Cosmos DB ─────────────────────────────────────────────────────────────────

@description('Globally unique name for the Cosmos DB account.')
param cosmosAccountName string

@description('Cosmos DB database name.')
param cosmosDatabaseName string = 'ragdb'

@description('Cosmos DB container name.')
param cosmosContainerName string = 'documents'

@description('Cosmos DB container name for monthly RAG reports.')
param cosmosRaportitContainerName string = 'kuukausiraportit'

@description('Luo vektori-indeksoidun kuukausiraportit-containerin. Vaatii että EnableNoSQLVectorSearch on ensin aktivoitu tilillä. Aseta true vasta toisella deploymentilla.')
param deployVectorContainer bool = false

// ── MS SQL ────────────────────────────────────────────────────────────────────

@secure()
@description('MS SQL Server admin password. Pass via pipeline secret (AZURE_SQL_ADMIN_PASSWORD).')
param sqlAdminPassword string = ''

@description('MS SQL Server admin login.')
param sqlAdminLogin string = 'sqladmin'

@description('MS SQL database name.')
param sqlDatabaseName string = 'llmgateway'

// ── Circuit Breaker ───────────────────────────────────────────────────────────

param circuitBreakerFailureThreshold int = 5
param circuitBreakerBreakDurationSeconds int = 30

// ── Log Analytics Workspace ───────────────────────────────────────────────────

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${appName}-logs'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

// ── Application Insights ──────────────────────────────────────────────────────

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${appName}-insights'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
  }
}

// ── Azure OpenAI ──────────────────────────────────────────────────────────────

resource openAI 'Microsoft.CognitiveServices/accounts@2024-04-01-preview' = {
  name: openAIResourceName
  location: location
  kind: 'OpenAI'
  sku: { name: 'S0' }
  properties: {
    publicNetworkAccess: 'Enabled'
  }
}

// Model deploymentit luodaan peräkkäin — Azure ei salli rinnakkaisia deploymentteja samalle tilille
resource gpt4Deployment 'Microsoft.CognitiveServices/accounts/deployments@2024-04-01-preview' = {
  parent: openAI
  name: gpt4DeploymentName
  sku: {
    name: 'Standard'
    capacity: gpt4Capacity
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: 'gpt-4o'
      version: gpt4ModelVersion
    }
  }
}

resource gpt4oMiniDeployment 'Microsoft.CognitiveServices/accounts/deployments@2024-04-01-preview' = {
  parent: openAI
  name: gpt4oMiniDeploymentName
  sku: {
    name: 'Standard'
    capacity: gpt4oMiniCapacity
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: 'gpt-4o-mini'
      version: gpt4oMiniModelVersion
    }
  }
  dependsOn: [gpt4Deployment] // peräkkäinen luonti vaaditaan
}

resource embeddingDeployment 'Microsoft.CognitiveServices/accounts/deployments@2024-04-01-preview' = {
  parent: openAI
  name: embeddingDeploymentName
  sku: {
    name: 'GlobalStandard'  // text-embedding-3-small vaatii GlobalStandard Sweden Centralissa
    capacity: embeddingCapacity
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: 'text-embedding-3-small'
      version: embeddingModelVersion
    }
  }
  dependsOn: [gpt4oMiniDeployment] // peräkkäinen luonti vaaditaan
}

// ── Cosmos DB ─────────────────────────────────────────────────────────────────

resource cosmosAccount 'Microsoft.DocumentDB/databaseAccounts@2024-05-15-preview' = {
  name: cosmosAccountName
  location: location
  kind: 'GlobalDocumentDB'
  properties: {
    databaseAccountOfferType: 'Standard'
    locations: [
      {
        locationName: location
        failoverPriority: 0
        isZoneRedundant: false
      }
    ]
    consistencyPolicy: {
      defaultConsistencyLevel: 'Session'
    }
    // Vaaditaan VectorDistance-vektorihaulle kuukausiraportit-säiliössä
    capabilities: [
      { name: 'EnableNoSQLVectorSearch' }
    ]
  }
}

resource cosmosDatabase 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-05-15' = {
  parent: cosmosAccount
  name: cosmosDatabaseName
  properties: {
    resource: { id: cosmosDatabaseName }
  }
}

resource cosmosContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-05-15' = {
  parent: cosmosDatabase
  name: cosmosContainerName
  properties: {
    resource: {
      id: cosmosContainerName
      partitionKey: {
        paths: ['/id']
        kind: 'Hash'
      }
    }
  }
}

// Kuukausiraportit-säiliö vektori-indeksillä RAG-hakua varten.
// Luodaan vasta toisella deploymentilla (deployVectorContainer=true) kun capability on aktivoitunut.
// text-embedding-3-small tuottaa 1536-ulotteisen float32-vektorin.
resource raportitContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-05-15-preview' = if (deployVectorContainer) {
  parent: cosmosDatabase
  name: cosmosRaportitContainerName
  properties: {
    resource: {
      id: cosmosRaportitContainerName
      partitionKey: {
        paths: ['/id']
        kind: 'Hash'
      }
      vectorEmbeddingPolicy: {
        vectorEmbeddings: [
          {
            path: '/embedding'
            dataType: 'float32'
            dimensions: 1536
            distanceFunction: 'cosine'
          }
        ]
      }
      indexingPolicy: {
        // Kaikki polut indeksoidaan oletuksena — embedding-polku suljetaan erikseen pois
        includedPaths: [
          { path: '/*' }
        ]
        excludedPaths: [
          { path: '/embedding/*' }
        ]
        vectorIndexes: [
          {
            path: '/embedding'
            type: 'quantizedFlat'
          }
        ]
      }
    }
  }
}

// ── Azure SQL Server ──────────────────────────────────────────────────────────

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: '${appName}-sql'
  location: location
  properties: {
    administratorLogin: sqlAdminLogin
    administratorLoginPassword: sqlAdminPassword
    publicNetworkAccess: 'Enabled'
  }
}

// Salli Azure-sisäiset yhteydet (0.0.0.0 → 0.0.0.0 = Azure services firewall rule)
resource sqlFirewallAzureServices 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sqlServer
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

// Basic-tier (~5 €/kk), 2 GB
resource sqlDatabase 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: sqlDatabaseName
  location: location
  sku: {
    name: 'Basic'
    tier: 'Basic'
  }
}

// ── App Service Plan ──────────────────────────────────────────────────────────

resource appServicePlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: '${appName}-plan'
  location: location
  sku: {
    name: sku
  }
  kind: 'linux'
  properties: {
    reserved: true // pakollinen Linux-hostingille
  }
}

// ── App Service (LlmGateway) ──────────────────────────────────────────────────

resource webApp 'Microsoft.Web/sites@2023-12-01' = {
  name: appName
  location: location
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      http20Enabled: true
      minTlsVersion: '1.2'

      // ASP.NET Core lukee asetukset ympäristömuuttujista.
      // Sisäkkäiset JSON-avaimet erotetaan kahdella alaviivalla (__).
      appSettings: [
        { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }

        // API key auth
        { name: 'ApiKey__Key', value: gatewayApiKey }

        // Azure OpenAI — endpoint ja avain luetaan luodusta resurssista
        { name: 'AzureOpenAI__Endpoint',      value: openAI.properties.endpoint }
        { name: 'AzureOpenAI__ApiKey',         value: openAI.listKeys().key1 }
        { name: 'AzureOpenAI__ApiVersion',     value: azureOpenAIApiVersion }
        { name: 'AzureOpenAI__TimeoutMs',      value: string(azureOpenAITimeoutMs) }
        { name: 'AzureOpenAI__MaxRetries',     value: string(azureOpenAIMaxRetries) }
        { name: 'AzureOpenAI__RetryDelayMs',   value: string(azureOpenAIRetryDelayMs) }
        { name: 'AzureOpenAI__Deployments__gpt4',      value: gpt4DeploymentName }
        { name: 'AzureOpenAI__Deployments__gpt4oMini', value: gpt4oMiniDeploymentName }
        { name: 'AzureOpenAI__EmbeddingDeployment',    value: embeddingDeploymentName }

        // Policies
        { name: 'Policies__chat_default__PrimaryModel',  value: 'gpt4oMini' }
        { name: 'Policies__critical__PrimaryModel',       value: 'gpt4' }
        { name: 'Policies__critical__Fallbacks__0',       value: 'gpt4oMini' }
        { name: 'Policies__tools__PrimaryModel',          value: 'gpt4' }
        { name: 'Policies__tools__ToolsEnabled',          value: 'true' }
        { name: 'Policies__tools__QueryBackend',          value: 'cosmos' }
        { name: 'Policies__tools_sql__PrimaryModel',      value: 'gpt4' }
        { name: 'Policies__tools_sql__ToolsEnabled',      value: 'true' }
        { name: 'Policies__tools_sql__QueryBackend',      value: 'mssql' }
        { name: 'Policies__rag__PrimaryModel',            value: 'gpt4' }
        { name: 'Policies__rag__RagEnabled',              value: 'true' }
        { name: 'Policies__rag__QueryBackend',            value: 'cosmos' }

        // Cosmos DB — connection string luetaan luodusta resurssista
        { name: 'CosmosRag__ConnectionString', value: cosmosAccount.listConnectionStrings().connectionStrings[0].connectionString }
        { name: 'CosmosRag__DatabaseName',     value: cosmosDatabaseName }
        { name: 'CosmosRag__ContainerName',    value: cosmosContainerName }

        // RAG-palvelu — kuukausiraportit-säiliö semanttiseen hakuun
        { name: 'Rag__DatabaseName',           value: cosmosDatabaseName }
        { name: 'Rag__RaportitContainerName',  value: cosmosRaportitContainerName }
        { name: 'Rag__TopK',                   value: '3' }

        // MS SQL
        { name: 'Sql__ConnectionString', value: 'Server=${sqlServer.properties.fullyQualifiedDomainName};Database=${sqlDatabaseName};User Id=${sqlAdminLogin};Password=${sqlAdminPassword};Encrypt=True;TrustServerCertificate=False;' }

        // Circuit Breaker
        { name: 'CircuitBreaker__FailureThreshold',      value: string(circuitBreakerFailureThreshold) }
        { name: 'CircuitBreaker__BreakDurationSeconds',  value: string(circuitBreakerBreakDurationSeconds) }

        // Application Insights
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING',        value: appInsights.properties.ConnectionString }
        { name: 'ApplicationInsightsAgent_EXTENSION_VERSION',   value: '~3' }
      ]
    }
  }
}

// ── Storage Account (SyncFunction vaatii) ────────────────────────────────────

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

// ── Consumption Plan (SyncFunction, serverless ~0 €/kk idle) ─────────────────
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

// ── Function App (SyncFunction) ───────────────────────────────────────────────

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

        // Cosmos DB — sama resurssi kuin App Servicella
        { name: 'CosmosRag__ConnectionString', value: cosmosAccount.listConnectionStrings().connectionStrings[0].connectionString }
        { name: 'CosmosRag__DatabaseName',     value: cosmosDatabaseName }
        { name: 'CosmosRag__ContainerName',    value: cosmosContainerName }

        // MS SQL — sama server kuin App Servicella
        { name: 'Sql__ConnectionString', value: 'Server=${sqlServer.properties.fullyQualifiedDomainName};Database=${sqlDatabaseName};User Id=${sqlAdminLogin};Password=${sqlAdminPassword};Encrypt=True;TrustServerCertificate=False;' }

        // Kuukausiraporttien generointi — Azure OpenAI (sama resurssi kuin App Servicella)
        { name: 'MonthlyReport__AzureOpenAIEndpoint',       value: openAI.properties.endpoint }
        { name: 'MonthlyReport__AzureOpenAIApiKey',         value: openAI.listKeys().key1 }
        { name: 'MonthlyReport__ApiVersion',                value: azureOpenAIApiVersion }
        { name: 'MonthlyReport__CompletionDeploymentName',  value: gpt4oMiniDeploymentName }
        { name: 'MonthlyReport__EmbeddingDeploymentName',   value: embeddingDeploymentName }
        { name: 'MonthlyReport__ReportContainerName',       value: cosmosRaportitContainerName }

        // Application Insights — sama instanssi kuin App Servicella
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
      ]
    }
  }
}

// ── Outputs ───────────────────────────────────────────────────────────────────

output appUrl string = 'https://${webApp.properties.defaultHostName}'
output functionAppUrl string = 'https://${functionApp.properties.defaultHostName}'
output appInsightsName string = appInsights.name
