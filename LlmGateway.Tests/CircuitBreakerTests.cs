using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LlmGateway.Tests;

public class CircuitBreakerTests
{
    private static InMemoryCircuitBreaker Create(int threshold = 3, int breakSeconds = 30) =>
        new(Options.Create(new CircuitBreakerOptions
        {
            FailureThreshold = threshold,
            BreakDurationSeconds = breakSeconds
        }), NullLogger<InMemoryCircuitBreaker>.Instance);

    [Fact]
    public void IsOpen_ForNewKey_ReturnsFalse()
    {
        var cb = Create();
        Assert.False(cb.IsOpen("gpt4oMini"));
    }

    [Fact]
    public void IsOpen_BelowFailureThreshold_ReturnsFalse()
    {
        var cb = Create(threshold: 3);
        cb.RecordFailure("gpt4oMini");
        cb.RecordFailure("gpt4oMini");
        Assert.False(cb.IsOpen("gpt4oMini"));
    }

    [Fact]
    public void RecordFailure_AtThreshold_Opens()
    {
        var cb = Create(threshold: 3);
        cb.RecordFailure("gpt4oMini");
        cb.RecordFailure("gpt4oMini");
        cb.RecordFailure("gpt4oMini");
        Assert.True(cb.IsOpen("gpt4oMini"));
    }

    [Fact]
    public void RecordSuccess_ResetsFailureCount()
    {
        // 2 failures, then success, then 2 more failures: should still be closed (threshold=3)
        var cb = Create(threshold: 3);
        cb.RecordFailure("gpt4oMini");
        cb.RecordFailure("gpt4oMini");
        cb.RecordSuccess("gpt4oMini");
        cb.RecordFailure("gpt4oMini");
        cb.RecordFailure("gpt4oMini");
        Assert.False(cb.IsOpen("gpt4oMini"));
    }

    [Fact]
    public void IsOpen_AfterBreakDurationExpires_TransitionsToHalfOpen()
    {
        // BreakDurationSeconds=0 → OpenUntil=UtcNow, which is already past when checked
        var cb = Create(threshold: 1, breakSeconds: 0);
        cb.RecordFailure("gpt4oMini");
        // Circuit was opened, but break duration is 0 → immediately half-open
        Assert.False(cb.IsOpen("gpt4oMini"));
    }

    [Fact]
    public void DifferentKeys_HaveIndependentState()
    {
        var cb = Create(threshold: 2);
        cb.RecordFailure("gpt4");
        cb.RecordFailure("gpt4");
        Assert.True(cb.IsOpen("gpt4"));
        Assert.False(cb.IsOpen("gpt4oMini"));
    }

    [Fact]
    public void RecordSuccess_WhenPreviouslyOpen_ResetsState()
    {
        // Simulate: circuit opens, then RecordSuccess is called (e.g. after half-open probe succeeds)
        var cb = Create(threshold: 1, breakSeconds: 0);
        cb.RecordFailure("gpt4oMini"); // threshold hit, opens
        cb.RecordSuccess("gpt4oMini"); // explicit reset
        Assert.False(cb.IsOpen("gpt4oMini"));
    }
}
