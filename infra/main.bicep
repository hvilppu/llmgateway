// LlmGateway — Azure App Service infrastructure
// Deploy: az deployment group create --resource-group <rg> --template-file infra/main.bicep --parameters infra/main.bicepparam

@description('Globally unique name for the App Service. Used as the hostname: <appName>.azurewebsites.net')
param appName string

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

// ── Cosmos DB ─────────────────────────────────────────────────────────────────

@description('Globally unique name for the Cosmos DB account.')
param cosmosAccountName string

@description('Cosmos DB database name.')
param cosmosDatabaseName string = 'ragdb'

@description('Cosmos DB container name.')
param cosmosContainerName string = 'documents'

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

// ── Cosmos DB ─────────────────────────────────────────────────────────────────

resource cosmosAccount 'Microsoft.DocumentDB/databaseAccounts@2024-05-15' = {
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

// ── App Service ───────────────────────────────────────────────────────────────

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

        // Cosmos DB — connection string luetaan luodusta resurssista
        { name: 'CosmosRag__ConnectionString', value: cosmosAccount.listConnectionStrings().connectionStrings[0].connectionString }
        { name: 'CosmosRag__DatabaseName',     value: cosmosDatabaseName }
        { name: 'CosmosRag__ContainerName',    value: cosmosContainerName }

        // MS SQL (tools_sql-policy)
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

// ── Azure SQL Server ──────────────────────────────────────────────────────────

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: '${appName}-sql'
  location: location
  properties: {
    administratorLogin: sqlAdminLogin
    administratorLoginPassword: sqlAdminPassword
    // Sallii Azure-palveluiden yhteyden (esim. App Service)
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

// ── Outputs ───────────────────────────────────────────────────────────────────

output appName string = webApp.name
output appUrl string = 'https://${webApp.properties.defaultHostName}'
output appInsightsName string = appInsights.name
output cosmosAccountName string = cosmosAccount.name
