// SyncFunction — parameter values for function.bicep
// HUOM: sqlAdminPassword jätetään tyhjäksi — pipeline ylikirjoittaa sen secretillä.

using './function.bicep'

param functionAppName = 'heikkis-sync-function-prod'  // muuta globaalisti uniikiksi
param appName         = 'heikkis-llmgateway-prod'     // sama kuin main.bicepparam:ssä
param cosmosAccountName = 'heikkis-cosmos-prod'       // sama kuin main.bicepparam:ssä

param location        = 'swedencentral'

// Seuraava arvo tulee pipeline-secretistä — EI koskaan oikeaa arvoa tähän:
param sqlAdminPassword = ''  // ylikirjoitetaan: --parameters sqlAdminPassword="<arvo>"

param cosmosDatabaseName  = 'ragdb'
param cosmosContainerName = 'documents'
param sqlAdminLogin       = 'sqladmin'
param sqlDatabaseName     = 'llmgateway'
