# llmgateway
ASP.NET Core 10 minimal API gateway for Azure OpenAI,  with retry, timeout and  circuit breaker 

# Infran Käyttöönotto-järjestys

# 1. Luo Azure OpenAI -resurssi
  az cognitiveservices account create \
    --name my-openai-resource \
    --resource-group rg-llmgateway-prod \
    --kind OpenAI \
    --sku S0 \
    --location swedencentral

  # 2. Luo model deploymentit
  az cognitiveservices account deployment create \
    --name my-openai-resource \
    --resource-group rg-llmgateway-prod \
    --deployment-name gpt4-deployment \
    --model-name gpt-4 \
    --model-version "turbo-2024-04-09" \
    --model-format OpenAI \
    --sku-capacity 10 \
    --sku-name Standard

  az cognitiveservices account deployment create \
    --name my-openai-resource \
    --resource-group rg-llmgateway-prod \
    --deployment-name gpt4o-mini-deployment \
    --model-name gpt-4o-mini \
    --model-version "2024-07-18" \
    --model-format OpenAI \
    --sku-capacity 20 \
    --sku-name Standard

# 1. Luo Azure OpenAI -resurssi
  az cognitiveservices account create \
    --name my-openai-resource \
    --resource-group rg-llmgateway-prod \
    --kind OpenAI \
    --sku S0 \
    --location swedencentral

# 2. Luo model deploymentit
  az cognitiveservices account deployment create \
    --name my-openai-resource \
    --resource-group rg-llmgateway-prod \
    --deployment-name gpt4-deployment \
    --model-name gpt-4 \
    --model-version "turbo-2024-04-09" \
    --model-format OpenAI \
    --sku-capacity 10 \
    --sku-name Standard

  az cognitiveservices account deployment create \
    --name my-openai-resource \
    --resource-group rg-llmgateway-prod \
    --deployment-name gpt4o-mini-deployment \
    --model-name gpt-4o-mini \
    --model-version "2024-07-18" \
    --model-format OpenAI \
    --sku-capacity 20 \
    --sku-name Standard

# 3. Sen jälkeen päivitä main.bicepparam:
  param azureOpenAIEndpoint     = 'https://my-openai-resource.openai.azure.com/'   
  param gpt4DeploymentName      = 'gpt4-deployment'
  param gpt4oMiniDeploymentName = 'gpt4o-mini-deployment'

  # Kerran manuaalisesti tai GitHub Actions infra.yml:llä

  az group create --name rg-llmgateway-prod --location swedencentral
  az deployment group create \
    --resource-group rg-llmgateway-prod \
    --template-file infra/main.bicep \
    --parameters infra/main.bicepparam \
    --parameters azureOpenAIApiKey=<avaimesi>

 # 4. Aseta GitHub Secrets / Variables

  ┌──────────┬──────────────────────────────────┬──────────────────────────────┐   
  │  Tyyppi  │               Nimi               │             Arvo             │   
  ├──────────┼──────────────────────────────────┼──────────────────────────────┤   
  │          │                                  │ Lataa Azure Portalista → App │   
  │ Secret   │ AZURE_WEBAPP_PUBLISH_PROFILE     │  Service → Get publish       │   
  │          │                                  │ profile                      │   
  ├──────────┼──────────────────────────────────┼──────────────────────────────┤   
  │ Secret   │ AZURE_OPENAI_API_KEY             │ Azure OpenAI -avain          │   
  ├──────────┼──────────────────────────────────┼──────────────────────────────┤   
  │ Secret   │ AZURE_CLIENT_ID / TENANT_ID /    │ Service principal            │   
  │          │ SUBSCRIPTION_ID                  │ (infra.yml)                  │   
  ├──────────┼──────────────────────────────────┼──────────────────────────────┤   
  │ Variable │ AZURE_WEBAPP_NAME                │ llmgateway-prod              │   
  ├──────────┼──────────────────────────────────┼──────────────────────────────┤   
  │ Variable │ AZURE_RESOURCE_GROUP             │ rg-llmgateway-prod           │   
  └──────────┴──────────────────────────────────┴──────────────────────────────┘   

# 5. Push main-haaraan → deploy.yml käynnistyy automaattisesti

  ---
  Muista päivittää main.bicepparam

  param appName              = 'llmgateway-prod'   ← oltava globaalisti uniikki    
  param azureOpenAIEndpoint  = 'https://YOUR-RESOURCE...'
  param gpt4DeploymentName   = 'YOUR-DEPLOYMENT'
