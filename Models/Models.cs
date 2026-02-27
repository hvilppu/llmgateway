
namespace LlmGateway;

// Asiakkaan lähettämä pyyntö gatewaylle.
// Message on käyttäjän viesti, ConversationId mahdollistaa keskusteluhistorian myöhemmin.
public class ChatRequest
{
    public string Message { get; set; } = string.Empty;

    // esim. "chat_default" tai "critical"
    public string? Policy { get; set; }

    public string? ConversationId { get; set; }
}

// Gatewayn palauttama vastaus asiakkaalle.
// Sisältää mallin tuottaman vastauksen, käytetyn mallin nimen, token-kulutuksen ja pyynnön id:n.
public class ChatResponse
{
    public string Reply { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public UsageInfo? Usage { get; set; }
    public string RequestId { get; set; } = string.Empty;
}

// Token-kulutustiedot yhdelle pyynnolle.
// PromptTokens = syötteen tokenit, CompletionTokens = vastauksen tokenit.
public class UsageInfo
{
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int TotalTokens { get; set; }
}

// Azure OpenAI REST API:n palauttama vastausobjekti (minimi-toteutus).
// Deserialisoidaan suoraan Azure:n JSON-vastauksesta.
public class AzureChatCompletionResponse
{
    public string Id { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public AzureUsage? Usage { get; set; }
    public List<AzureChoice> Choices { get; set; } = new();
}

// Azure OpenAI:n token-kulutustiedot. Kenttien nimet vastaavat Azure:n JSON-kenttien muotoa.
public class AzureUsage
{
    public int Prompt_tokens { get; set; }
    public int Completion_tokens { get; set; }
    public int Total_tokens { get; set; }
}

// Yksi vaihtoehto Azure:n vastauksessa. Tavallisesti Choices-listassa on yksi alkio.
// Finish_reason kertoo miksi generointi päättyi (esim. "stop" tai "tool_calls").
public class AzureChoice
{
    public int Index { get; set; }
    public AzureMessage? Message { get; set; }
    public string Finish_reason { get; set; } = string.Empty;
}

// Azure:n palauttama yksittäinen viesti. Role on tyypillisesti "assistant".
// Tool calling -vastauksessa Content voi olla null ja ToolCalls sisältää kutsut.
public class AzureMessage
{
    public string Role { get; set; } = string.Empty;
    public string? Content { get; set; }
    public List<AzureToolCall>? Tool_calls { get; set; }
}

// Yksittäinen tool call -kutsu LLM:n vastauksessa.
public class AzureToolCall
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = "function";
    public AzureFunctionCall Function { get; set; } = new();
}

// Tool callin funktio-osa: nimi ja argumentit JSON-merkkijonona.
public class AzureFunctionCall
{
    public string Name { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
}

// GetRawCompletionAsync:n palauttama raaka vastaus — sisältää finish_reason:n,
// sisällön tai tool_calls:n sekä token-kulutuksen.
public class AzureRawCompletion
{
    public string FinishReason { get; set; } = string.Empty;
    public string? Content { get; set; }
    public List<AzureToolCall>? ToolCalls { get; set; }
    public AzureUsage? Usage { get; set; }
    public string Model { get; set; } = string.Empty;
}