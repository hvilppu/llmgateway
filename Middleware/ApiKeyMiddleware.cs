using Microsoft.Extensions.Options;

namespace LlmGateway.Middleware;

// Konfiguraatio API-avaimelle. Sidotaan appsettings-osioon "ApiKey".
public class ApiKeyOptions
{
    // Salainen avain joka asiakkaan pitää lähettää X-Api-Key -headerissa.
    // Jos tyhjä, kaikki pyynnöt hylätään (fail-closed).
    public string Key { get; set; } = string.Empty;
}

// Middleware tarkistaa X-Api-Key -headerin jokaiselta pyynnöltä.
// /openapi/* -polku ohitetaan (kehitystyökalu, ei tuotantokäytössä).
public class ApiKeyMiddleware
{
    private const string ApiKeyHeader = "X-Api-Key";

    private readonly RequestDelegate _next;
    private readonly string _validKey;
    private readonly ILogger<ApiKeyMiddleware> _logger;

    public ApiKeyMiddleware(
        RequestDelegate next,
        IOptions<ApiKeyOptions> options,
        ILogger<ApiKeyMiddleware> logger)
    {
        _next = next;
        _validKey = options.Value.Key;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // OpenAPI-schema ohitetaan — kehitystyökalu, ei suojauksen piirissä
        if (context.Request.Path.StartsWithSegments("/openapi"))
        {
            await _next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue(ApiKeyHeader, out var providedKey)
            || string.IsNullOrEmpty(_validKey)
            || providedKey != _validKey)
        {
            _logger.LogWarning("Unauthorized request. Path={Path}, IP={IP}, HasHeader={HasHeader}",
                context.Request.Path,
                context.Connection.RemoteIpAddress,
                context.Request.Headers.ContainsKey(ApiKeyHeader));

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new
            {
                title = "Unauthorized",
                detail = "Valid X-Api-Key header required"
            });
            return;
        }

        await _next(context);
    }
}
