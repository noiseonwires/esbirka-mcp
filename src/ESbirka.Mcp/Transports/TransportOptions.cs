namespace ESbirka.Mcp.Transports;

public sealed class TransportOptions
{
    public Uri? Proxy { get; set; }
    public string UserAgent { get; set; } = ESbirka.Client.HttpContentFetcher.DefaultUserAgent;
}