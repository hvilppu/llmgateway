// LlmGateway — parameter values for main.bicep
// HUOM: azureOpenAIApiKey jätetään tyhjäksi — pipeline ylikirjoittaa sen secretillä.

using './main.bicep'

param appName              = 'xyz-llmgateway-prod'       // muuta yksilölliseksi
param location             = 'swedencentral'
param sku                  = 'B1'

param azureOpenAIApiKey    = ''  // EI täytetä tähän — tulee pipeline-secretistä (AZURE_OPENAI_API_KEY)

param azureOpenAIEndpoint  = 'https://swedencentral.api.cognitive.microsoft.com/'
param azureOpenAIApiVersion = '2024-02-15-preview'

param gpt4DeploymentName      = 'gpt4-deployment'
param gpt4oMiniDeploymentName = 'gpt4o-mini-deployment'

// Embedding (RAG / function calling)
param embeddingDeploymentName = 'text-embedding-3-small'

// Cosmos DB — function calling -agenttilooppia varten (tools-policy)
// param cosmosConnectionString  = 'AccountEndpoint=https://...;AccountKey=...;'  // EI tähän — käytä pipeline-secretiä
param cosmosDatabaseName      = 'ragdb'
param cosmosContainerName     = 'documents'
// param cosmosTopK            = 5

// Jätä oletusarvot tai ylikirjoita tarvittaessa:
// param azureOpenAITimeoutMs             = 15000
// param azureOpenAIMaxRetries            = 2
// param azureOpenAIRetryDelayMs          = 500
// param circuitBreakerFailureThreshold   = 5
// param circuitBreakerBreakDurationSeconds = 30
