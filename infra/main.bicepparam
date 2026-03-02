// LlmGateway — parameter values for main.bicep

using './main.bicep'

param appName            = 'heikkis-llmgateway-prod'   // muuta globaalisti uniikiksi
param location           = 'swedencentral'
param sku                = 'B1'

param gatewayApiKey      = ''  // EI täytetä tähän — tulee pipeline-secretistä (GATEWAY_API_KEY)

// Azure OpenAI — resurssi ja deploymentit luodaan automaattisesti Bicepissä
param openAIResourceName      = 'heikkis-openai-prod'        // muuta globaalisti uniikiksi
param gpt4DeploymentName      = 'gpt4-deployment'
param gpt4oMiniDeploymentName = 'gpt4o-mini-deployment'

// Cosmos DB — tili, tietokanta ja container luodaan automaattisesti Bicepissä
param cosmosAccountName   = 'heikkis-cosmos-prod'            // muuta globaalisti uniikiksi
param cosmosDatabaseName  = 'ragdb'
param cosmosContainerName = 'documents'

// MS SQL
param sqlAdminLogin   = 'sqladmin'
param sqlDatabaseName = 'llmgateway'
// param sqlAdminPassword = ''  // EI täytetä tähän — tulee pipeline-secretistä (AZURE_SQL_ADMIN_PASSWORD)

// Jätä oletusarvot tai ylikirjoita tarvittaessa:
// param azureOpenAIApiVersion              = '2024-02-15-preview'
// param azureOpenAITimeoutMs               = 15000
// param azureOpenAIMaxRetries              = 2
// param azureOpenAIRetryDelayMs            = 500
// param gpt4ModelVersion                   = '2024-11-20'
// param gpt4oMiniModelVersion              = '2024-07-18'
// param gpt4Capacity                       = 10
// param gpt4oMiniCapacity                  = 20
// param circuitBreakerFailureThreshold     = 5
// param circuitBreakerBreakDurationSeconds = 30
