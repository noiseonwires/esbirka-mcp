using System.Net;
using ESbirka.Client;

namespace ESbirka.Tests;

public sealed class TransportTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("MyLegalClient/1.0 (+https://example.com/contact)")]
    public async Task UserAgentAppliesToGetAndPost(string? userAgent)
    {
        var methods = new List<HttpMethod>();
        using var http = new HttpClient(new StubHandler(request =>
        {
            methods.Add(request.Method);
            Assert.Equal(userAgent ?? HttpContentFetcher.DefaultUserAgent, request.Headers.UserAgent.ToString());
            return new(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }));
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OtherClient/2.0");
        var fetcher = userAgent is null ? new HttpContentFetcher(http) : new HttpContentFetcher(http) { UserAgent = userAgent };
        var uri = new Uri("https://e-sbirka.gov.cz/");
        await fetcher.FetchAsync(uri, ESbirkaContentKind.Json);
        await fetcher.PostJsonAsync(uri, "{}");
        Assert.Equal([HttpMethod.Get, HttpMethod.Post], methods);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Client/1.0\r\nInjected: value")]
    public async Task InvalidUserAgentIsRejectedBeforeSending(string userAgent)
    {
        var calls = 0;
        using var http = new HttpClient(new StubHandler(_ =>
        {
            calls++;
            return new(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }));
        var fetcher = new HttpContentFetcher(http) { UserAgent = userAgent };
        var uri = new Uri("https://e-sbirka.gov.cz/");
        var getError = await Record.ExceptionAsync(() => fetcher.FetchAsync(uri, ESbirkaContentKind.Json));
        var postError = await Record.ExceptionAsync(() => fetcher.PostJsonAsync(uri, "{}"));
        Assert.True(getError is ArgumentException or FormatException);
        Assert.True(postError is ArgumentException or FormatException);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task JsonPostPreservesBodyAndUsesResponseLimits()
    {
        var uri = new Uri("https://e-sbirka.gov.cz/sbr-cache/jednoducha-vyhledavani");
        const string json = "{\"fulltext\":\"89/2012\",\"start\":1,\"pocet\":2}";
        using var http = new HttpClient(new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(uri, request.RequestUri);
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            Assert.Equal(json, request.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"pocetCelkem\":0,\"seznam\":[]}") };
        }));
        var response = await new HttpContentFetcher(http).PostJsonAsync(uri, json);
        Assert.Equal(200, response.StatusCode);
        var error = await Assert.ThrowsAsync<ESbirkaException>(() => new HttpContentFetcher(http, 10).PostJsonAsync(uri, json));
        Assert.Equal("response_too_large", error.Code);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new HttpContentFetcher(http).PostJsonAsync(uri, json, cancelled.Token));
    }

    [Fact]
    public async Task HttpSuccessPreservesEncodedUriAndResponseMetadata()
    {
        Uri? observed = null;
        using var http = new HttpClient(new StubHandler(request =>
        {
            observed = request.RequestUri;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}") };
            response.Headers.ETag = new("\"fixture\"");
            return response;
        }));
        var options = new ESbirkaClientOptions();
        var fetcher = new HttpContentFetcher(http);
        var uri = LawAddress.Endpoint(options.BaseUri, "/sb/2022/65");
        var result = await fetcher.FetchAsync(uri, ESbirkaContentKind.Json);
        Assert.Equal(uri.AbsoluteUri, observed!.AbsoluteUri);
        Assert.Contains("%2Fsb%2F", observed.AbsoluteUri);
        Assert.Equal("\"fixture\"", result.ETag);
    }

    [Fact]
    public async Task OversizedContentAndCancellationArePreserved()
    {
        using var http = new HttpClient(new StubHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(new string('x', 100)) }));
        var options = new ESbirkaClientOptions { MaximumResponseBytes = 10 };
        var fetcher = new HttpContentFetcher(http, 10);
        var error = await Assert.ThrowsAsync<ESbirkaException>(() => fetcher.FetchAsync(new("https://e-sbirka.gov.cz"), ESbirkaContentKind.Json));
        Assert.Equal("response_too_large", error.Code);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetcher.FetchAsync(new("https://e-sbirka.gov.cz"), ESbirkaContentKind.Json, cancelled.Token));
    }

    internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }
}