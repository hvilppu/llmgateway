namespace LlmGateway.Tests;

internal class FakeCircuitBreaker : ICircuitBreaker
{
    public bool IsOpenResult { get; set; }
    public int SuccessCount { get; private set; }
    public int FailureCount { get; private set; }

    public bool IsOpen(string key) => IsOpenResult;
    public void RecordSuccess(string key) => SuccessCount++;
    public void RecordFailure(string key) => FailureCount++;
}

internal class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();
    public int CallCount { get; private set; }

    public void Enqueue(HttpResponseMessage response) => _responses.Enqueue(response);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        if (_responses.Count == 0)
            throw new InvalidOperationException("No more queued responses in FakeHttpMessageHandler");
        return Task.FromResult(_responses.Dequeue());
    }
}
