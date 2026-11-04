using Microsoft.AspNetCore.Http;

namespace Sift.Tests;

public class CrossSiteWriteGuardTests
{
    private static async Task<(int StatusCode, bool ReachedEndpoint)> SendAsync(
        string method,
        string? origin = null,
        string? contentType = "application/json",
        bool withSiftHeader = true)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("127.0.0.1", 5199);
        context.Request.ContentType = contentType;
        if (origin is not null)
        {
            context.Request.Headers.Origin = origin;
        }

        if (withSiftHeader)
        {
            context.Request.Headers[ServerCommand.RequestHeaderName] = "1";
        }

        var reachedEndpoint = false;
        await ServerCommand.RejectCrossSiteWrites(context, () =>
        {
            reachedEndpoint = true;
            return Task.CompletedTask;
        });

        return (context.Response.StatusCode, reachedEndpoint);
    }

    [Fact]
    public async Task SameOriginJsonWithHeader_IsAllowed()
    {
        Assert.True((await SendAsync("POST", origin: "http://127.0.0.1:5199")).ReachedEndpoint);
    }

    [Fact]
    public async Task CliRequestWithoutOrigin_IsAllowed()
    {
        Assert.True((await SendAsync("POST")).ReachedEndpoint);
    }

    [Theory]
    [InlineData("https://evil.example", "application/json", true)]
    [InlineData("http://127.0.0.1:5199", "text/plain", true)]
    [InlineData("http://127.0.0.1:5199", "application/json", false)]
    [InlineData(null, "application/x-www-form-urlencoded", false)]
    public async Task ForgeableWrites_AreRejected(string? origin, string contentType, bool withSiftHeader)
    {
        var (statusCode, reachedEndpoint) = await SendAsync("POST", origin, contentType, withSiftHeader);

        Assert.False(reachedEndpoint);
        Assert.Equal(StatusCodes.Status403Forbidden, statusCode);
    }

    [Fact]
    public async Task Reads_AreNotGuarded()
    {
        Assert.True((await SendAsync("GET", origin: "https://evil.example", contentType: null, withSiftHeader: false)).ReachedEndpoint);
    }
}
