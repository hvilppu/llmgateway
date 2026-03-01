
using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace LlmGateway.Infrastructure;

// Circuit breakerin konfiguraatio appsettingsistä.
// FailureThreshold: montako peräkkäistä virhettä ennen kuin piiri avataan.
// BreakDurationSeconds: kuinka kauan piiri pysyy auki ennen half-open -tilaa.
public class CircuitBreakerOptions
{
    public int FailureThreshold { get; set; } = 5;
    public int BreakDurationSeconds { get; set; } = 30;
}

// Circuit breaker -rajapinta. key on tyypillisesti deployment/mallinimi,
// jolloin eri malleilla on omat tilansa.
public interface ICircuitBreaker
{
    bool IsOpen(string key);       // Palauttaa true jos piiri on auki (pyynnöt estetty)
    void RecordSuccess(string key); // Kutsutaan onnistuneen kutsun jälkeen — nollaa virhelaskurin
    void RecordFailure(string key); // Kutsutaan epäonnistuneen kutsun jälkeen — kasvattaa laskuria
}

// Heitetään kun pyyntö yritetään tehdä ja piiri on auki.
// Kutsuva koodi (esim. ChatEndpoints) voi käsitellä tämän erikseen ja palauttaa 503.
public class CircuitBreakerOpenException : Exception
{
    public string ModelKey { get; }

    public CircuitBreakerOpenException(string modelKey)
        : base($"Circuit breaker is open for {modelKey}")
    {
        ModelKey = modelKey;
    }
}

// Sisäinen tila yhdelle avaimelle (mallille). Ei näytetä ulospäin.
// FailureCount: peräkkäisten virheiden määrä. OpenUntil: auki-tilan päättymisaika.
internal class CircuitBreakerEntry
{
    public int FailureCount;
    public DateTimeOffset? OpenUntil;
}

// ICircuitBreaker-toteutus joka pitää tilan muistissa ConcurrentDictionaryssa.
// Sopii yksittäiselle prosessille — ei jaa tilaa usean instanssin kesken.
public class InMemoryCircuitBreaker : ICircuitBreaker
{
    private readonly ConcurrentDictionary<string, CircuitBreakerEntry> _entries = new();
    private readonly CircuitBreakerOptions _options;
    private readonly ILogger<InMemoryCircuitBreaker> _logger;

    public InMemoryCircuitBreaker(
        IOptions<CircuitBreakerOptions> options,
        ILogger<InMemoryCircuitBreaker> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    // Palauttaa true jos avain on auki-tilassa eli pyynnöt tulee hylätä.
    // Tarkistaa myös onko cooldown ohi — jos on, siirtyy half-open-tilaan (FailureCount nollataan).
    public bool IsOpen(string key)
    {
        var entry = _entries.GetOrAdd(key, _ => new CircuitBreakerEntry());
        if (entry.OpenUntil is { } openUntil)
        {
            if (openUntil > DateTimeOffset.UtcNow)
            {
                _logger.LogWarning("Circuit breaker OPEN for {ModelKey} until {OpenUntil}", key, openUntil);
                return true;
            }

            // Cooldown ohi → half-open: päästetään yksi pyyntö läpi kokeiluna
            entry.OpenUntil = null;
            entry.FailureCount = 0;
            _logger.LogInformation("Circuit breaker HALF-OPEN for {ModelKey}", key);
        }

        return false;
    }

    // Nollaa tilan onnistuneen kutsun jälkeen. Kutsutaan aina kun Azure vastaa 2xx.
    public void RecordSuccess(string key)
    {
        var entry = _entries.GetOrAdd(key, _ => new CircuitBreakerEntry());
        if (entry.FailureCount > 0 || entry.OpenUntil != null)
        {
            _logger.LogInformation("Circuit breaker SUCCESS for {ModelKey}, resetting state", key);
        }

        entry.FailureCount = 0;
        entry.OpenUntil = null;
    }

    // Kasvattaa virhelaskuria. Jos FailureThreshold ylittyy, asettaa OpenUntil-ajan
    // ja estää kaikki pyynnöt BreakDurationSeconds ajaksi.
    public void RecordFailure(string key)
    {
        var entry = _entries.GetOrAdd(key, _ => new CircuitBreakerEntry());
        entry.FailureCount++;

        _logger.LogWarning("Circuit breaker failure for {ModelKey}. FailureCount={FailureCount}",
            key, entry.FailureCount);

        if (entry.FailureCount >= _options.FailureThreshold && entry.OpenUntil == null)
        {
            entry.OpenUntil = DateTimeOffset.UtcNow.AddSeconds(_options.BreakDurationSeconds);
            _logger.LogError("Circuit breaker OPENED for {ModelKey} until {OpenUntil}",
                key, entry.OpenUntil);
        }
    }
}