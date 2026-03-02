// SyncFunction — parameter values for function.bicep
// HUOM: cosmosConnectionString ja sqlAdminPassword jätetään tyhjiksi — pipeline ylikirjoittaa ne secreteillä.

using './function.bicep'

param functionAppName = 'heikkis-sync-function-prod'  // muuta globaalisti uniikiksi
param appName         = 'heikkis-llmgateway-prod'     // sama kuin main.bicepparam:ssä

param location        = 'swedencentral'

// Seuraavat arvot tulevat pipeline-secreteistä — EI koskaan oikeita arvoja tähän:
param cosmosConnectionString = ''  // ylikirjoitetaan: --parameters cosmosConnectionString="<arvo>"
param sqlAdminPassword       = ''  // ylikirjoitetaan: --parameters sqlAdminPassword="<arvo>"

param cosmosDatabaseName  = 'ragdb'
param cosmosContainerName = 'documents'
param sqlAdminLogin       = 'sqladmin'
param sqlDatabaseName     = 'llmgateway'
