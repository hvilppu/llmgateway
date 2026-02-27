# llmgateway
ASP.NET Core 10 minimal API gateway for Azure OpenAI,  with retry, timeout and  circuit breaker 


# Infran Käyttöönotto-järjestys

  1. Luo resource group ja provisioi infra

  # Kerran manuaalisesti tai GitHub Actions infra.yml:llä
  az group create --name rg-llmgateway-prod --location swedencentral
  az deployment group create \
    --resource-group rg-llmgateway-prod \
    --template-file infra/main.bicep \
    --parameters infra/main.bicepparam \
    --parameters azureOpenAIApiKey=<avaimesi>

  2. Aseta GitHub Secrets / Variables

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

  3. Push main-haaraan → deploy.yml käynnistyy automaattisesti

  ---
  Muista päivittää main.bicepparam

  param appName              = 'llmgateway-prod'   ← oltava globaalisti uniikki    
  param azureOpenAIEndpoint  = 'https://YOUR-RESOURCE...'
  param gpt4DeploymentName   = 'YOUR-DEPLOYMENT'
