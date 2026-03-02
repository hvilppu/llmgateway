// LlmGateway — parameter values for main.bicep

using './main.bicep'

// ── Globaalisti uniikit nimet — muuta omiksi ──────────────────────────────────
param appName            = 'heikkis-llmgateway-prod'
param functionAppName    = 'heikkis-sync-function-prod'
param openAIResourceName = 'heikkis-openai-prod'
param cosmosAccountName  = 'heikkis-cosmos-prod'

// ── Alue ──────────────────────────────────────────────────────────────────────
param location = 'swedencentral'
param sku      = 'B1'

// ── Salaisuudet — EI täytetä tähän, tulevat pipeline-secreteistä ─────────────
param gatewayApiKey    = ''  // ← GATEWAY_API_KEY
param sqlAdminPassword = ''  // ← AZURE_SQL_ADMIN_PASSWORD

// ── MS SQL ────────────────────────────────────────────────────────────────────
param sqlAdminLogin   = 'sqladmin'
param sqlDatabaseName = 'llmgateway'

// ── Cosmos DB ─────────────────────────────────────────────────────────────────
param cosmosDatabaseName  = 'ragdb'
param cosmosContainerName = 'documents'

// ── OpenAI model deploymentit ─────────────────────────────────────────────────
param gpt4DeploymentName      = 'gpt4-deployment'
param gpt4oMiniDeploymentName = 'gpt4o-mini-deployment'

// Jätä oletusarvot tai ylikirjoita tarvittaessa:
// param gpt4ModelVersion                   = '2024-11-20'
// param gpt4oMiniModelVersion              = '2024-07-18'
// param gpt4Capacity                       = 10
// param gpt4oMiniCapacity                  = 20
// param azureOpenAIApiVersion              = '2024-02-15-preview'
// param azureOpenAITimeoutMs               = 15000
// param azureOpenAIMaxRetries              = 2
// param azureOpenAIRetryDelayMs            = 500
// param circuitBreakerFailureThreshold     = 5
// param circuitBreakerBreakDurationSeconds = 30
