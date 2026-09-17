using ESbirka.Client;

namespace ESbirka.Tests;

public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ESBIRKA_LIVE_TESTS") != "1")
            Skip = "Set ESBIRKA_LIVE_TESTS=1 to contact the public e-Sbirka service.";
    }
}

public sealed class LiveTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "esbirka-live-" + Guid.NewGuid().ToString("N"));
    private readonly HttpClient _http = new();
    private readonly ESbirkaRequestCoordinator _coordinator = new();

    private ESbirkaClient Create()
    {
        var options = new ESbirkaClientOptions { CachePath = Path.Combine(_directory, "cache.db") };
        return new(new HttpContentFetcher(_http), new SqliteESbirkaCache(options), options, _coordinator);
    }

    [LiveFact]
    [Trait("Category", "Live")]
    public async Task SearchUsesWebsitePageIndices()
    {
        using var client = Create();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        const string query = "ob\u010dansk\u00fd z\u00e1kon\u00edk";
        var first = await client.SearchAsync(query, pageSize: 4, cancellationToken: timeout.Token);
        Assert.Equal(4, first.Items.Count);
        Assert.Contains(first.Items, item => item.Collection == "sb" && item.PublicationNumber == "89/2012");
        Assert.True(first.HasMore);
        var second = await client.SearchAsync(query, page: 1, pageSize: 2, cancellationToken: timeout.Token);
        Assert.Equal(first.Items.Skip(2).Select(item => item.StableUrl), second.Items.Select(item => item.StableUrl));
    }

    [LiveFact]
    [Trait("Category", "Live")]
    public async Task SmallLawCurrentPromulgatedHistoricalAndFutureVersions()
    {
        using var client = Create();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var selector = new LegalProvisionSelector(Section: "1", Paragraph: "1", Letter: "a");
        var current = await client.GetProvisionAsync("65/2022", new(), selector, new(IncludeXhtml: true), timeout.Token);
        Assert.EndsWith("/par_1/odst_1/pism_a", current!.Eli);
        Assert.Contains("65/2022", current.Citation);
        var promulgated = await client.GetProvisionAsync("65/2022", new(AsPromulgated: true), selector, cancellationToken: timeout.Token);
        Assert.Equal("VYHLASENE", promulgated!.ResolvedVersion.Type);
        Assert.NotEqual(current.PlainText, promulgated.PlainText);
        var historical = await client.GetProvisionAsync("65/2022", new(new(2023, 5, 15)), new(Section: "1"), cancellationToken: timeout.Token);
        Assert.Equal(new DateOnly(2023, 4, 1), historical!.ResolvedVersion.EffectiveFrom);
        Assert.Equal(new DateOnly(2023, 6, 30), historical.ResolvedVersion.EffectiveThrough);
        var future = await client.GetProvisionAsync("65/2022", new(new(2030, 1, 1)), selector, cancellationToken: timeout.Token);
        Assert.Equal(new DateOnly(2030, 1, 1), future!.RequestedVersion.AsOf);
        Assert.Equal(current.ResolvedVersion.StableUrl, future.ResolvedVersion.StableUrl);
        var versions = await client.GetVersionsAsync("65/2022", timeout.Token);
        Assert.Contains(versions, version => version.Type == "VYHLASENE");
        Assert.Contains(versions, version => version.Type == "MINULE");
        var error = await Assert.ThrowsAsync<ESbirkaException>(() => client.GetProvisionAsync("65/2022", new(new(2022, 1, 1)), selector, cancellationToken: timeout.Token));
        Assert.Equal("version_not_available", error.Code);
    }

    [LiveFact]
    [Trait("Category", "Live")]
    public async Task CivilCodeSubtreeCrossesPageBoundaryAndChapterHasCorrectScope()
    {
        using var client = Create();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var section = await client.GetProvisionAsync("89/2012", new(), new(Section: "310"), cancellationToken: timeout.Token);
        Assert.Contains(section!.Fragments, fragment => fragment.Eli.EndsWith("/par_310/pism_b", StringComparison.Ordinal));
        Assert.Contains(section.Fragments, fragment => fragment.Eli.EndsWith("/par_310/pism_c", StringComparison.Ordinal));
        var chapter = await client.GetProvisionAsync("89/2012", new(), new(Part: "1", Chapter: "2"), new(RefreshPolicy: RefreshPolicy.CacheOnly), timeout.Token);
        Assert.EndsWith("/cast_1/hlava_2", chapter!.Eli);
        Assert.All(chapter.Fragments, fragment => Assert.True(fragment.Eli == chapter.Eli || fragment.Eli.StartsWith(chapter.Eli + "/", StringComparison.Ordinal)));
    }

    public void Dispose()
    {
        _http.Dispose();
        _coordinator.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}