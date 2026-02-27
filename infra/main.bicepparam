// LlmGateway — parameter values for main.bicep
// HUOM: azureOpenAIApiKey jätetään tyhjäksi — pipeline ylikirjoittaa sen secretillä.

using './main.bicep'

param appName              = 'llmgateway-prod'       // muuta yksilölliseksi
param location             = 'swedencentral'
param sku                  = 'B1'

param azureOpenAIApiKey    = ''  // EI täytetä tähän — tulee pipeline-secretistä (AZURE_OPENAI_API_KEY)

param azureOpenAIEndpoint  = 'https://YOUR-RESOURCE.openai.azure.com/'
param azureOpenAIApiVersion = '2024-02-15-preview'

param gpt4DeploymentName      = 'YOUR-GPT4-DEPLOYMENT-NAME'
param gpt4oMiniDeploymentName = 'YOUR-GPT4O-MINI-DEPLOYMENT-NAME'

// Jätä oletusarvot tai ylikirjoita tarvittaessa:
// param azureOpenAITimeoutMs             = 15000
// param azureOpenAIMaxRetries            = 2
// param azureOpenAIRetryDelayMs          = 500
// param circuitBreakerFailureThreshold   = 5
// param circuitBreakerBreakDurationSeconds = 30
