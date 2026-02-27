// LlmGateway — Azure App Service infrastructure
// Deploy: az deployment group create --resource-group <rg> --template-file infra/main.bicep --parameters infra/main.bicepparam

@description('Globally unique name for the App Service. Used as the hostname: <appName>.azurewebsites.net')
param appName string

@description('Azure region. Defaults to resource group location.')
param location string = resourceGroup().location

@description('App Service Plan SKU. B1 = ~13 €/kk, P1v3 = ~70 €/kk.')
@allowed(['F1', 'B1', 'B2', 'P1v3', 'P2v3'])
param sku string = 'B1'

// ── Azure OpenAI ──────────────────────────────────────────────────────────────

@description('Azure OpenAI endpoint, e.g. https://my-resource.openai.azure.com/')
param azureOpenAIEndpoint string

@secure()
@description('Gateway API key. Clients must send this in X-Api-Key header. Pass via --parameters or pipeline secret.')
param gatewayApiKey string

@secure()
@description('Azure OpenAI API key. Pass via --parameters or pipeline secret — never store in params file.')
param azureOpenAIApiKey string

@description('Azure OpenAI API version.')
param azureOpenAIApiVersion string = '2024-02-15-preview'

param azureOpenAITimeoutMs int = 15000
param azureOpenAIMaxRetries int = 2
param azureOpenAIRetryDelayMs int = 500

@description('Azure deployment name for gpt4.')
param gpt4DeploymentName string

@description('Azure deployment name for gpt4oMini.')
param gpt4oMiniDeploymentName string

@description('Azure OpenAI embedding deployment name, e.g. text-embedding-3-small.')
param embeddingDeploymentName string = 'text-embedding-3-small'

// ── Cosmos DB (RAG) ───────────────────────────────────────────────────────────

@secure()
@description('Cosmos DB primary connection string. Pass via --parameters or pipeline secret.')
param cosmosConnectionString string = ''

@description('Cosmos DB database name.')
param cosmosDatabaseName string = 'ragdb'

@description('Cosmos DB container name (must have vector index on embedding field).')
param cosmosContainerName string = 'documents'

@description('Number of documents to retrieve per query.')
param cosmosTopK int = 5

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

        // Azure OpenAI
        { name: 'AzureOpenAI__Endpoint',      value: azureOpenAIEndpoint }
        { name: 'AzureOpenAI__ApiKey',         value: azureOpenAIApiKey }
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

        // Cosmos DB RAG
        { name: 'CosmosRag__ConnectionString', value: cosmosConnectionString }
        { name: 'CosmosRag__DatabaseName',     value: cosmosDatabaseName }
        { name: 'CosmosRag__ContainerName',    value: cosmosContainerName }
        { name: 'CosmosRag__TopK',             value: string(cosmosTopK) }

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

// ── Outputs ───────────────────────────────────────────────────────────────────

output appName string = webApp.name
output appUrl string = 'https://${webApp.properties.defaultHostName}'
output appInsightsName string = appInsights.name
