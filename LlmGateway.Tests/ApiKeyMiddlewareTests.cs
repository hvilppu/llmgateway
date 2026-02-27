using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LlmGateway.Tests;

public class ApiKeyMiddlewareTests
{
    private static ApiKeyMiddleware Create(string configuredKey, RequestDelegate next) =>
        new ApiKeyMiddleware(
            next,
            Options.Create(new ApiKeyOptions { Key = configuredKey }),
            NullLogger<ApiKeyMiddleware>.Instance);

    private static DefaultHttpContext NewContext(string? apiKey = null, string path = "/api/chat")
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        ctx.Request.Path = path;
        if (apiKey is not null)
            ctx.Request.Headers["X-Api-Key"] = apiKey;
        return ctx;
    }

    [Fact]
    public async Task MissingHeader_Returns401()
    {
        var mw = Create("secret", _ => Task.CompletedTask);
        var ctx = NewContext(apiKey: null);

        await mw.InvokeAsync(ctx);

        Assert.Equal(401, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task WrongKey_Returns401()
    {
        var mw = Create("secret", _ => Task.CompletedTask);
        var ctx = NewContext(apiKey: "wrong");

        await mw.InvokeAsync(ctx);

        Assert.Equal(401, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task ValidKey_CallsNext()
    {
        bool nextCalled = false;
        var mw = Create("secret", _ => { nextCalled = true; return Task.CompletedTask; });
        var ctx = NewContext(apiKey: "secret");

        await mw.InvokeAsync(ctx);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task OpenApiPath_SkipsAuthEvenWithoutHeader()
    {
        bool nextCalled = false;
        var mw = Create("secret", _ => { nextCalled = true; return Task.CompletedTask; });
        var ctx = NewContext(apiKey: null, path: "/openapi/v1.json");

        await mw.InvokeAsync(ctx);

        Assert.True(nextCalled);
        Assert.Equal(200, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task EmptyConfiguredKey_Returns401EvenWithHeader()
    {
        // Jos ApiKey:Key ei ole konfiguroitu, kaikki hylätään (fail-closed)
        var mw = Create("", _ => Task.CompletedTask);
        var ctx = NewContext(apiKey: "anything");

        await mw.InvokeAsync(ctx);

        Assert.Equal(401, ctx.Response.StatusCode);
    }
}
