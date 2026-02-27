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

    // Palauttaa järjestetyn listan modelKey:stä: [primary, fallback1, fallback2, ...].
    // Tuntemattomat modelKey:t konfigista jätetään pois lokivaroituksella.
    IReadOnlyList<string> ResolveModelChain(ChatRequest request);
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

    // Palauttaa ensimmäisen modelKey:n ketjusta. Delegoi ResolveModelChain:lle.
    public string ResolveModelKey(ChatRequest request) => ResolveModelChain(request)[0];

    public IReadOnlyList<string> ResolveModelChain(ChatRequest request)
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

        var chain = new List<string>();

        // Primary model — pakollinen, virhe jos puuttuu deployments-konfigista
        if (!_openAIOptions.Deployments.ContainsKey(policy.PrimaryModel))
        {
            _logger.LogError("Policy {PolicyName} refers to unknown modelKey {ModelKey}", policyName, policy.PrimaryModel);
            throw new InvalidOperationException($"Unknown modelKey '{policy.PrimaryModel}' in policy '{policyName}'");
        }

        chain.Add(policy.PrimaryModel);

        // Fallback-mallit — ohitetaan jos ei löydy deployments-konfigista
        if (policy.Fallbacks is not null)
        {
            foreach (var fallback in policy.Fallbacks)
            {
                if (_openAIOptions.Deployments.ContainsKey(fallback))
                    chain.Add(fallback);
                else
                    _logger.LogWarning("Fallback modelKey {ModelKey} in policy {PolicyName} not found in deployments, skipping", fallback, policyName);
            }
        }

        _logger.LogInformation("Model chain resolved. Policy={Policy}, Chain={Chain}",
            policyName, string.Join("->", chain));

        return chain;
    }
}
