using System.Text.Json;
using ESbirka.Client;

namespace ESbirka.Tests;

public sealed class ClientTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "esbirka-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ESbirkaRequestCoordinator _coordinator = new();
    private readonly FakeFetcher _fetcher = new();
    private readonly TestClock _clock = new();
    private ESbirkaClientOptions Options => new() { CachePath = Path.Combine(_directory, "cache.db"), RequestDelay = TimeSpan.Zero, RetryCount = 0 };

    private ESbirkaClient Create() => new(_fetcher, new SqliteESbirkaCache(Options), Options, _coordinator, timeProvider: _clock);

    [Fact]
    public async Task SearchUsesPageIndexAndPreservesCollectionsWithoutCaching()
    {
        using var client = Create();
        var result = await client.SearchAsync("  89/2012  ", page: 1, pageSize: 2);
        using var request = JsonDocument.Parse(_fetcher.SearchBody!);
        Assert.Equal("89/2012", request.RootElement.GetProperty("fulltext").GetString());
        Assert.Equal(1, request.RootElement.GetProperty("start").GetInt32());
        Assert.Equal(2, request.RootElement.GetProperty("pocet").GetInt32());
        Assert.Equal("+relevance", request.RootElement.GetProperty("razeni")[0].GetString());
        Assert.Equal("https://e-sbirka.gov.cz/sbr-cache/jednoducha-vyhledavani", _fetcher.SearchUri!.AbsoluteUri);
        Assert.Equal("89/2012", result.Query);
        Assert.Equal(1, result.Page);
        Assert.Equal(2, result.PageSize);
        Assert.Equal(5, result.TotalCount);
        Assert.True(result.HasMore);
        Assert.Equal(_clock.GetUtcNow(), result.RetrievedAt);
        Assert.Equal("89/2012", result.Items[0].PublicationNumber);
        Assert.Equal("sb", result.Items[0].Collection);
        Assert.Equal(new DateOnly(2012, 3, 22), result.Items[0].PublishedOn);
        Assert.Null(result.Items[1].PublicationNumber);
        Assert.Equal("sm", result.Items[1].Collection);
        Assert.Equal("89/2012 Sb. m. s.", result.Items[1].PublicationCode);
        Assert.Equal("/sm/2012/89/0000-00-00", result.Items[1].StableUrl);
        Assert.Equal("FUTURE_STATUS", result.Items[1].Status);
        await client.SearchAsync("89/2012", page: 1, pageSize: 2);
        Assert.Equal(2, _fetcher.Calls);
        Assert.Empty(await new SqliteESbirkaCache(Options).ListAsync("89/2012"));
    }

    [Theory]
    [InlineData("", 0, 20)]
    [InlineData(" ", 0, 20)]
    [InlineData(null, 0, 20)]
    [InlineData("law", -1, 20)]
    [InlineData("law", 0, 0)]
    [InlineData("law", 0, 101)]
    [InlineData("law", int.MaxValue, 20)]
    public async Task SearchRejectsInvalidArgumentsBeforeFetching(string? query, int page, int pageSize)
    {
        using var client = Create();
        var error = await Assert.ThrowsAsync<ESbirkaException>(() => client.SearchAsync(query!, page, pageSize));
        Assert.Equal("invalid_argument", error.Code);
        Assert.Equal(0, _fetcher.Calls);
    }

    [Fact]
    public async Task SearchHandlesEmptyAndLastPagesAndCancellation()
    {
        using var client = Create();
        _fetcher.SearchJson = """{"pocetCelkem":0,"seznam":[]}""";
        var empty = await client.SearchAsync("nonexistent");
        Assert.Empty(empty.Items);
        Assert.False(empty.HasMore);
        _fetcher.SearchJson = """{"pocetCelkem":2,"seznam":[]}""";
        Assert.False((await client.SearchAsync("past last page", 1, 2)).HasMore);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SearchAsync("law", cancellationToken: cancellation.Token));
        var error = await Assert.ThrowsAsync<ESbirkaException>(() => client.SearchAsync(new string('x', 2001)));
        Assert.Equal("invalid_argument", error.Code);
        Assert.Equal(2, _fetcher.Calls);
    }

    [Theory]
    [InlineData("<html>login</html>")]
    [InlineData("{}")]
    [InlineData("{\"pocetCelkem\":-1,\"seznam\":[]}")]
    [InlineData("{\"pocetCelkem\":1,\"seznam\":[{}]}")]
    public async Task SearchRejectsMalformedResponses(string json)
    {
        _fetcher.SearchJson = json;
        using var client = Create();
        var error = await Assert.ThrowsAsync<ESbirkaException>(() => client.SearchAsync("law"));
        Assert.Equal("upstream_contract_changed", error.Code);
    }

    [Theory]
    [InlineData(400, "invalid_argument")]
    [InlineData(404, "upstream_unavailable")]
    [InlineData(429, "upstream_unavailable")]
    [InlineData(503, "upstream_unavailable")]
    public async Task SearchMapsUpstreamErrors(int status, string code)
    {
        _fetcher.SearchStatus = status;
        using var client = Create();
        var error = await Assert.ThrowsAsync<ESbirkaException>(() => client.SearchAsync("law"));
        Assert.Equal(code, error.Code);
    }

    [Theory]
    [InlineData("https://example.com/sb/2012/89")]
    [InlineData("//example.com/sb/2012/89")]
    [InlineData("/sb/2012/99/2026-01-01")]
    [InlineData("/sb/2012/89/2026-13-01")]
    [InlineData("/sb/2012/89/2026-01-01/extra")]
    public async Task SearchRejectsUnsafeOrMismatchedDocumentPaths(string path)
    {
        var response = System.Text.Json.Nodes.JsonNode.Parse(_fetcher.SearchJson)!;
        response["seznam"]![0]!["staleUrl"] = path;
        _fetcher.SearchJson = response.ToJsonString();
        using var client = Create();
        var error = await Assert.ThrowsAsync<ESbirkaException>(() => client.SearchAsync("law"));
        Assert.Equal("upstream_contract_changed", error.Code);
    }

    [Fact]
    public async Task SearchEnforcesPageAndResponseSizeLimits()
    {
        using var client = Create();
        var pageError = await Assert.ThrowsAsync<ESbirkaException>(() => client.SearchAsync("law", pageSize: 1));
        Assert.Equal("upstream_contract_changed", pageError.Code);
        var options = Options;
        options.MaximumResponseBytes = 32;
        using var limited = new ESbirkaClient(_fetcher, new SqliteESbirkaCache(options), options, _coordinator);
        var sizeError = await Assert.ThrowsAsync<ESbirkaException>(() => limited.SearchAsync("law"));
        Assert.Equal("response_too_large", sizeError.Code);
    }

    [Fact]
    public async Task ConcurrentMissesDownloadOnceAndRestartUsesPersistentSnapshot()
    {
        _fetcher.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var client = Create())
        {
            var requests = Enumerable.Range(0, 20).Select(_ => client.GetProvisionAsync("65/2022 Sb.", new(), new(Section: "1"))).ToArray();
            Assert.Equal(1, _fetcher.Calls);
            _fetcher.Release.SetResult();
            var results = await Task.WhenAll(requests);
            Assert.All(results, result => Assert.Equal(3, result!.Fragments.Count));
            Assert.Equal(3, _fetcher.Calls);
        }
        using var restarted = Create();
        var cached = await restarted.GetProvisionAsync("65/2022", new(), new(Section: "1", Letter: "c"), new(RefreshPolicy: RefreshPolicy.CacheOnly));
        Assert.Equal("hit", cached!.Cache.Status);
        Assert.Equal(3, _fetcher.Calls);
    }

    [Fact]
    public async Task CancelledWaiterDoesNotCancelSharedDocumentLoad()
    {
        _fetcher.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = Create();
        var owner = client.GetProvisionAsync("65/2022", new(), new(Section: "1"));
        using var cancellation = new CancellationTokenSource();
        var waiter = client.GetProvisionAsync("65/2022", new(), new(Section: "1"), cancellationToken: cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        _fetcher.Release.SetResult();
        Assert.NotNull(await owner);
        Assert.Equal(3, _fetcher.Calls);
    }

    [Fact]
    public async Task CacheOnlyMissDoesNotContactUpstream()
    {
        using var client = Create();
        var error = await Assert.ThrowsAsync<ESbirkaException>(() => client.GetProvisionAsync("65/2022", new(), new(Section: "1"), new(RefreshPolicy: RefreshPolicy.CacheOnly)));
        Assert.Equal("cache_miss", error.Code);
        Assert.Equal(0, _fetcher.Calls);
    }

    [Fact]
    public async Task FutureResolutionIsRevalidatedAfterTtlEvenWhenItsCanonicalSnapshotIsCached()
    {
        using var client = Create();
        var selector = new LawVersionSelector(new(2030, 1, 1));
        var original = await client.GetProvisionAsync("65/2022", selector, new(Section: "1"));
        _clock.Advance(TimeSpan.FromHours(3));
        _fetcher.CanonicalDate = "2026-10-01";
        var refreshed = await client.GetProvisionAsync("65/2022", selector, new(Section: "1"));
        Assert.NotEqual(original!.ResolvedVersion.StableUrl, refreshed!.ResolvedVersion.StableUrl);
        Assert.Equal(selector, refreshed.RequestedVersion);
    }

    [Fact]
    public async Task AliasMovementPreservesHistoryAndFailedRefreshCannotPublishPartialSnapshot()
    {
        using var client = Create();
        var original = await client.GetProvisionAsync("65/2022", new(), new(Section: "1"));
        _fetcher.CanonicalDate = "2026-01-01";
        _fetcher.FailSecondPage = true;
        var stale = await client.GetProvisionAsync("65/2022", new(), new(Section: "1"), new(RefreshPolicy: RefreshPolicy.Revalidate, AllowStale: true));
        Assert.True(stale!.Cache.IsStale);
        Assert.Equal(original!.ResolvedVersion.StableUrl, stale.ResolvedVersion.StableUrl);
        _fetcher.FailSecondPage = false;
        await client.RevalidateCurrentAsync("65/2022");
        var historical = await client.GetProvisionAsync("65/2022", new(new(2025, 9, 3)), new(Section: "1"), new(RefreshPolicy: RefreshPolicy.CacheOnly));
        Assert.Equal(original.PlainText, historical!.PlainText);
        var current = await client.GetProvisionAsync("65/2022", new(), new(Section: "1"));
        Assert.EndsWith("2026-01-01", current!.ResolvedVersion.StableUrl);
    }

    [Fact]
    public async Task StaleRequiresOptInAndHasMaximumAge()
    {
        using var client = Create();
        await client.GetProvisionAsync("65/2022", new(), new(Section: "1"));
        _clock.Advance(TimeSpan.FromHours(3));
        _fetcher.FailAll = true;
        await Assert.ThrowsAsync<ESbirkaException>(() => client.GetProvisionAsync("65/2022", new(), new(Section: "1")));
        Assert.True((await client.GetProvisionAsync("65/2022", new(), new(Section: "1"), new(AllowStale: true)))!.Cache.IsStale);
        _clock.Advance(TimeSpan.FromDays(2));
        await Assert.ThrowsAsync<ESbirkaException>(() => client.GetProvisionAsync("65/2022", new(), new(Section: "1"), new(AllowStale: true)));
    }

    [Fact]
    public async Task PurgesRespectScopeAndDryRun()
    {
        using var client = Create();
        await client.GetProvisionAsync("65/2022", new(), new(Section: "1"));
        await client.PurgeProvisionAsync("65/2022", new(), new(Section: "1"));
        await client.GetProvisionAsync("65/2022", new(), new(Section: "1"));
        Assert.Equal(3, _fetcher.Calls);
        var preview = await client.PurgeDocumentAsync("65/2022", true, dryRun: true);
        Assert.Equal(2, preview.Records);
        await client.GetProvisionAsync("65/2022", new(), new(Section: "1"));
        Assert.Equal(3, _fetcher.Calls);
        await client.PurgeVersionAsync("65/2022", new(2025, 9, 3));
        var error = await Assert.ThrowsAsync<ESbirkaException>(() => client.GetProvisionAsync("65/2022", new(), new(Section: "1"), new(RefreshPolicy: RefreshPolicy.CacheOnly)));
        Assert.Equal("cache_miss", error.Code);
    }

    [Fact]
    public async Task UnknownTypesReferencesAndUnnumberedBodiesSurvive()
    {
        using var client = Create();
        var provision = await client.GetProvisionAsync("65/2022", new(), new(Section: "2"), new(IncludeXhtml: true));
        Assert.Contains("unnumbered", provision!.PlainText);
        Assert.Equal("FutureType", provision.Fragments[1].Type);
        Assert.Single(provision.References);
        Assert.Contains("<p>", provision.Xhtml);
        var structure = await client.GetStructureAsync("65/2022", new());
        Assert.Equal(2, structure.Count);
        Assert.Equal("2", structure[1].Selector.Section);
    }

    [Fact]
    public async Task MetadataWithoutAmendmentsIsValid()
    {
        _fetcher.OmitAmendments = true;
        using var client = Create();
        var result = await client.GetProvisionAsync("65/2022", new(), new(Section: "1"));
        Assert.Empty(result!.ResolvedVersion.Amendments);
    }

    [Fact]
    public async Task InvalidPageVersionDoesNotBecomeCached()
    {
        using var client = Create();
        _fetcher.MixedVersion = true;
        var error = await Assert.ThrowsAsync<ESbirkaException>(() => client.GetProvisionAsync("65/2022", new(), new(Section: "1")));
        Assert.Equal("upstream_contract_changed", error.Code);
        Assert.Empty(await new SqliteESbirkaCache(Options).ListAsync("65/2022"));
    }

    public void Dispose()
    {
        _coordinator.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 16, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }

    internal sealed class FakeFetcher : IESbirkaContentFetcher
    {
        public int Calls;
        public string CanonicalDate = "2025-09-03";
        public bool FailSecondPage;
        public bool FailAll;
        public bool MixedVersion;
        public bool OmitAmendments;
        public TaskCompletionSource? Release;

                public string? SearchBody;
                public Uri? SearchUri;
                public int SearchStatus = 200;
                public string SearchJson = """
                        {"pocetCelkem":5,"seznam":[
                            {"staleUrl":"/sb/2012/89/2026-01-01","nazev":"Civil code","kodDokumentuSbirky":"89/2012 Sb.","stavDokumentuSbirky":"AKTUALNE_PLATNY","datum":"2012-03-22"},
                            {"staleUrl":"/sm/2012/89/0000-00-00","nazev":"Treaty","kodDokumentuSbirky":"89/2012 Sb. m. s.","stavDokumentuSbirky":"FUTURE_STATUS","datum":null}
                        ]}
                        """;

                public Task<ESbirkaFetchResponse> PostJsonAsync(Uri uri, string json, CancellationToken cancellationToken = default)
                {
                        cancellationToken.ThrowIfCancellationRequested();
                        Interlocked.Increment(ref Calls);
                        SearchBody = json;
                        SearchUri = uri;
                        return Task.FromResult(new ESbirkaFetchResponse(uri, uri, SearchStatus, SearchJson, "application/json"));
                }

        public async Task<ESbirkaFetchResponse> FetchAsync(Uri uri, ESbirkaContentKind contentKind, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            if (Release is not null) await Release.Task.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var second = uri.Query.Contains("=1", StringComparison.Ordinal);
            if (FailAll || (FailSecondPage && second)) throw new HttpRequestException("Fixture outage");
            var path = "/sb/2022/65/" + CanonicalDate;
            var eli = "/eli/cz" + path;
            string json;
            if (!uri.AbsolutePath.EndsWith("/fragmenty", StringComparison.Ordinal))
                json = JsonSerializer.Serialize(new { staleUrl = path, eli, typZneni = "AKTUALNI", datumUcinnostiZneniOd = CanonicalDate, datumUcinnostiZneniDo = (string?)null, nazev = "Fixture law", novely = Array.Empty<object>() });
            else
            {
                if (MixedVersion && second) eli = "/eli/cz/sb/2022/65/1999-01-01";
                object Fragment(long id, int depth, string suffix, string text, string type = "Paragraf", bool reference = false) => new
                {
                    id, eli = eli + "/dokument/norma" + suffix, staleUrl = path + "#" + suffix,
                    kodTypuFragmentu = type, hloubka = depth, uplnaCitace = "citation", zkracenaCitace = "label", jeUcinny = true,
                    xhtml = "<p>" + text + "</p>", odkazyZFragmentu = reference ? new object[] { new { kodTypuVazbyOdkazu = "INTUSTAN", cil = new { staleUrl = path + "#par_1" } } } : []
                };
                json = JsonSerializer.Serialize(new { pocetStranek = 2, seznam = second
                    ? new[] { Fragment(3, 3, "/par_1/pism_c", "c) text"), Fragment(4, 2, "/par_2", "section 2"), Fragment(5, 3, "/par_2/frag_5", "unnumbered", "FutureType", true) }
                    : new[] { Fragment(1, 2, "/par_1", "section 1"), Fragment(2, 3, "/par_1/pism_b", "b) text") } });
            }
            if (OmitAmendments)
            {
                var value = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
                value.Remove("novely");
                json = value.ToJsonString();
            }
            return new ESbirkaFetchResponse(uri, uri, 200, json, "application/json");
        }
    }
}