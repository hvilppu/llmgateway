using Microsoft.Extensions.Options;

namespace LlmGateway;

// Konfiguraatio kaikille policyille. Sidotaan appsettings-osioon "Policies".
public class PolicyOptions
{
    public Dictionary<string, PolicyConfig> Policies { get; set; } = new();
}

// Yhden policyn konfiguraatio.
// PrimaryModel viittaa AzureOpenAIOptions.Deployments-dictionaryn avaimeen.
public class PolicyConfig
{
    public string PrimaryModel { get; set; } = string.Empty;
    public List<string>? Fallbacks { get; set; } // myöhemmin käytettäväksi
}

// Rajapinta routing enginelle. Mahdollistaa mock-toteutuksen testeissä.
public interface IRoutingEngine
{
    // Palauttaa modelKeyn (esim. "gpt4oMini") pyynnön policyn perusteella.
    string ResolveModelKey(ChatRequest request);
}

// Valitsee oikean modelKeyn pyynnön Policy-kentän ja appsettings-konfiguraation perusteella.
public class RoutingEngine : IRoutingEngine
{
    private readonly PolicyOptions _policyOptions;
    private readonly AzureOpenAIOptions _openAIOptions;
    private readonly ILogger<RoutingEngine> _logger;

    private const string DefaultPolicy = "chat_default";

    public RoutingEngine(
        IOptions<PolicyOptions> policyOptions,
        IOptions<AzureOpenAIOptions> openAIOptions,
        ILogger<RoutingEngine> logger)
    {
        _policyOptions = policyOptions.Value;
        _openAIOptions = openAIOptions.Value;
        _logger = logger;
    }

    public string ResolveModelKey(ChatRequest request)
    {
        var policyName = request.Policy ?? DefaultPolicy;

        // Tuntematon policy → fallback defaulttiin
        if (!_policyOptions.Policies.TryGetValue(policyName, out var policy))
        {
            _logger.LogWarning("Unknown policy {PolicyName}, falling back to default", policyName);
            policyName = DefaultPolicy;
            policy = _policyOptions.Policies.GetValueOrDefault(DefaultPolicy)
                     ?? throw new InvalidOperationException("Default policy 'chat_default' not configured");
        }

        var modelKey = policy.PrimaryModel;

        // Varmistetaan että modelKey löytyy deployments-konfigista
        if (!_openAIOptions.Deployments.ContainsKey(modelKey))
        {
            _logger.LogError("Policy {PolicyName} refers to unknown modelKey {ModelKey}", policyName, modelKey);
            throw new InvalidOperationException($"Unknown modelKey '{modelKey}' in policy '{policyName}'");
        }

        _logger.LogInformation("Routing request. Policy={Policy}, ModelKey={ModelKey}", policyName, modelKey);

        return modelKey;
    }
}
