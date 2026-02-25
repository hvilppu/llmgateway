namespace LlmGateway;

// Sidotaan appsettings-osioon "AzureOpenAI". Rekisteröidään DI:hin IOptions<AzureOpenAIOptions>.
public class AzureOpenAIOptions
{
    public string Endpoint { get; set; } = string.Empty;       // https://YOUR-RESOURCE.openai.azure.com/
    public string ApiKey { get; set; } = string.Empty;
    public string DeploymentName { get; set; } = string.Empty; // Azure-portaalissa luotu deployment
    public string ApiVersion { get; set; } = "2024-02-15-preview";
    public int TimeoutMs { get; set; } = 15000;     // Per-kutsu timeout millisekunteina
    public int MaxRetries { get; set; } = 2;         // Uudelleenyritykset transientteihin virheisiin
    public int RetryDelayMs { get; set; } = 500;     // Perusviive ennen retrytä (kerrotaan attempt-numerolla)
}
