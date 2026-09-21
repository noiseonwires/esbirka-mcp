using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;

namespace ESbirka.Client;

public enum ESbirkaContentKind { Json, Html }

public sealed record ESbirkaFetchResponse(Uri RequestedUri, Uri FinalUri, int StatusCode,
    string Content, string? ContentType = null, string? ETag = null, DateTimeOffset? LastModified = null);

public interface IESbirkaContentFetcher
{
    Task<ESbirkaFetchResponse> FetchAsync(Uri uri, ESbirkaContentKind contentKind,
        CancellationToken cancellationToken = default);

    Task<ESbirkaFetchResponse> PostJsonAsync(Uri uri, string json,
        CancellationToken cancellationToken = default) =>
        throw new ESbirkaException("search_not_supported", "This content fetcher does not support JSON POST requests.");
}

public sealed class ESbirkaClientOptions
{
    public Uri BaseUri { get; set; } = new("https://e-sbirka.gov.cz/");
    public TimeSpan RequestDelay { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(90);
    public TimeSpan CurrentAliasTtl { get; set; } = TimeSpan.FromHours(2);
    public TimeSpan VersionListTtl { get; set; } = TimeSpan.FromHours(12);
    public TimeSpan NegativeCacheTtl { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan MaximumStaleAge { get; set; } = TimeSpan.FromHours(24);
    public int MaximumResponseBytes { get; set; } = 16 * 1024 * 1024;
    public int MaximumSnapshotBytes { get; set; } = 128 * 1024 * 1024;
    public int MaximumPages { get; set; } = 100;
    public int RetryCount { get; set; } = 2;
    public string CachePath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "esbirka", "cache.db");
    public long MaximumCacheBytes { get; set; } = 512L * 1024 * 1024;
    public long MemoryCacheBytes { get; set; } = 32L * 1024 * 1024;

    internal void Validate()
    {
        if (!BaseUri.IsAbsoluteUri || BaseUri.Scheme is not ("https" or "http") || !BaseUri.AbsolutePath.EndsWith('/')
            || RequestDelay < TimeSpan.Zero || RequestTimeout <= TimeSpan.Zero || CurrentAliasTtl < TimeSpan.Zero
            || VersionListTtl < TimeSpan.Zero || MaximumStaleAge < TimeSpan.Zero || NegativeCacheTtl <= TimeSpan.Zero
            || MaximumResponseBytes < 1 || MaximumSnapshotBytes < MaximumResponseBytes || MaximumPages < 1
            || RetryCount is < 0 or > 5 || MaximumCacheBytes < 1 || MemoryCacheBytes < 1)
            throw new ArgumentException("Invalid e-Sbirka options; sizes and timeouts must be positive and BaseUri must end in '/'.");
    }
}

public sealed class HttpContentFetcher(HttpClient httpClient, int maximumResponseBytes = 16 * 1024 * 1024) : IESbirkaContentFetcher
{
    public const string DefaultUserAgent = "Mozilla/5.0 ESbirka.Client/0.1";
    public string UserAgent { get; init; } = DefaultUserAgent;

    public async Task<ESbirkaFetchResponse> FetchAsync(Uri uri, ESbirkaContentKind contentKind, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        return await SendAsync(request, contentKind, cancellationToken);
    }

    public async Task<ESbirkaFetchResponse> PostJsonAsync(Uri uri, string json, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        return await SendAsync(request, ESbirkaContentKind.Json, cancellationToken);
    }

    private async Task<ESbirkaFetchResponse> SendAsync(HttpRequestMessage request, ESbirkaContentKind contentKind, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        ArgumentException.ThrowIfNullOrWhiteSpace(UserAgent);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", contentKind == ESbirkaContentKind.Json ? "application/json" : "text/html,application/xhtml+xml");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.Content.Headers.ContentLength > maximumResponseBytes)
            throw new ESbirkaException("response_too_large", "Upstream response exceeds configured limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(bytes, cancellationToken)) > 0)
        {
            if (buffer.Length + count > maximumResponseBytes)
                throw new ESbirkaException("response_too_large", "Upstream response exceeds configured limit.");
            buffer.Write(bytes, 0, count);
        }
        return new(uri, response.RequestMessage?.RequestUri ?? uri, (int)response.StatusCode,
            Encoding.UTF8.GetString(buffer.ToArray()), response.Content.Headers.ContentType?.MediaType,
            response.Headers.ETag?.ToString(), response.Content.Headers.LastModified);
    }

    public static void Validate(ESbirkaFetchResponse response, ESbirkaContentKind contentKind)
    {
        if (response.StatusCode is 400 or 404) return;
        if (response.StatusCode is < 200 or >= 300) throw new HttpRequestException($"HTTP {response.StatusCode}.", null, (System.Net.HttpStatusCode)response.StatusCode);
        if (string.IsNullOrWhiteSpace(response.Content)) throw new InvalidDataException("Empty upstream response.");
        if (contentKind == ESbirkaContentKind.Json)
        {
            using var document = JsonDocument.Parse(response.Content);
            if (document.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                throw new InvalidDataException("Expected a JSON object or array.");
        }
        else if (!response.Content.Contains("<html", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Expected an HTML document.");
    }
}

public sealed class ESbirkaRequestCoordinator : IDisposable
{
    private readonly SemaphoreSlim[] _documents = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private readonly SemaphoreSlim _remote = new(1, 1);
    private DateTimeOffset _nextRequest;
    public SemaphoreSlim ForDocument(string publicationNumber) => _documents[(uint)StringComparer.Ordinal.GetHashCode(publicationNumber) % (uint)_documents.Length];

    public async Task<T> FetchAsync<T>(Func<Task<T>> fetch, TimeSpan delay, CancellationToken cancellationToken)
    {
        await _remote.WaitAsync(cancellationToken);
        try
        {
            var remaining = _nextRequest - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining, cancellationToken);
            ESbirkaMetrics.RemoteRequests.Add(1);
            return await fetch();
        }
        finally
        {
            _nextRequest = DateTimeOffset.UtcNow + delay;
            _remote.Release();
        }
    }

    public void Dispose()
    {
        _remote.Dispose();
        foreach (var gate in _documents) gate.Dispose();
    }
}

internal static class ESbirkaMetrics
{
    private static readonly Meter Meter = new("ESbirka.Client", "0.1.1");
    internal static readonly Counter<long> RemoteRequests = Meter.CreateCounter<long>("esbirka.remote.requests");
    internal static readonly Counter<long> RemoteBytes = Meter.CreateCounter<long>("esbirka.remote.bytes");
    internal static readonly Counter<long> CacheReads = Meter.CreateCounter<long>("esbirka.cache.reads");
    internal static readonly Counter<long> RefreshFailures = Meter.CreateCounter<long>("esbirka.refresh.failures");
    internal static readonly Counter<long> ContractFailures = Meter.CreateCounter<long>("esbirka.contract.failures");
}