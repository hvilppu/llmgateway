using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LlmGateway.Tests;

public class RoutingEngineTests
{
    private static RoutingEngine Create(
        Dictionary<string, PolicyConfig> policies,
        Dictionary<string, string>? deployments = null)
    {
        deployments ??= new Dictionary<string, string>
        {
            { "gpt4oMini", "gpt4o-mini-deployment" },
            { "gpt4", "gpt4-deployment" }
        };

        return new RoutingEngine(
            Options.Create(new PolicyOptions { Policies = policies }),
            Options.Create(new AzureOpenAIOptions { Deployments = deployments }),
            NullLogger<RoutingEngine>.Instance);
    }

    private static Dictionary<string, PolicyConfig> DefaultPolicies() => new()
    {
        { "chat_default", new PolicyConfig { PrimaryModel = "gpt4oMini" } },
        { "critical",     new PolicyConfig { PrimaryModel = "gpt4" } }
    };

    [Fact]
    public void ResolveModelKey_KnownPolicy_ReturnsCorrectModelKey()
    {
        var engine = Create(DefaultPolicies());
        var result = engine.ResolveModelKey(new ChatRequest { Policy = "critical" });
        Assert.Equal("gpt4", result);
    }

    [Fact]
    public void ResolveModelKey_NullPolicy_FallsBackToDefault()
    {
        var engine = Create(DefaultPolicies());
        var result = engine.ResolveModelKey(new ChatRequest { Policy = null });
        Assert.Equal("gpt4oMini", result);
    }

    [Fact]
    public void ResolveModelKey_UnknownPolicy_FallsBackToDefault()
    {
        var engine = Create(DefaultPolicies());
        var result = engine.ResolveModelKey(new ChatRequest { Policy = "nonexistent" });
        Assert.Equal("gpt4oMini", result);
    }

    [Fact]
    public void ResolveModelKey_ModelKeyNotInDeployments_ThrowsInvalidOperationException()
    {
        var policies = new Dictionary<string, PolicyConfig>
        {
            { "chat_default", new PolicyConfig { PrimaryModel = "unknownModel" } }
        };
        var engine = Create(policies);

        Assert.Throws<InvalidOperationException>(() =>
            engine.ResolveModelKey(new ChatRequest()));
    }

    [Fact]
    public void ResolveModelKey_MissingDefaultPolicy_ThrowsInvalidOperationException()
    {
        // No "chat_default" policy configured at all
        var engine = Create(new Dictionary<string, PolicyConfig>());

        Assert.Throws<InvalidOperationException>(() =>
            engine.ResolveModelKey(new ChatRequest { Policy = null }));
    }
}
