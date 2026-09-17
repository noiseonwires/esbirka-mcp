using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ESbirka.Client;

public sealed class ESbirkaClient : IESbirkaClient, IESbirkaCacheAdministration, IDisposable
{
    private readonly IESbirkaContentFetcher _fetcher;
    private readonly IESbirkaCache _cache;
    private readonly ESbirkaClientOptions _options;
    private readonly ESbirkaRequestCoordinator _coordinator;
    private readonly ILogger<ESbirkaClient> _logger;
    private readonly TimeProvider _time;
    private readonly MemoryCache _indexes;
    private sealed record RawSnapshot(LawVersion Version, ESbirkaFetchResponse Metadata, IReadOnlyList<ESbirkaFetchResponse> Pages);
    private sealed record LoadedSnapshot(FragmentIndex Index, LawVersion Version, CacheFreshness Freshness);

    public ESbirkaClient(IESbirkaContentFetcher fetcher, IESbirkaCache cache, ESbirkaClientOptions options,
        ESbirkaRequestCoordinator coordinator, ILogger<ESbirkaClient>? logger = null, TimeProvider? timeProvider = null)
    {
        options.Validate();
        _fetcher = fetcher;
        _cache = cache;
        _options = options;
        _coordinator = coordinator;
        _logger = logger ?? NullLogger<ESbirkaClient>.Instance;
        _time = timeProvider ?? TimeProvider.System;
        _indexes = new(new MemoryCacheOptions { SizeLimit = options.MemoryCacheBytes });
    }

    public async Task<LawProvision?> GetProvisionAsync(string publicationNumber, LawVersionSelector version,
        LegalProvisionSelector provision, ProvisionOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new();
        var selectorKey = string.Join('/', LawAddress.Segments(provision));
        var number = LawAddress.NormalizePublicationNumber(publicationNumber);
        var loaded = await LoadAsync(number, version, options.RefreshPolicy, options.AllowStale, cancellationToken);
        var negativeKey = $"negative:{loaded.Version.StableUrl}:{loaded.Freshness.FetchedAt:O}:{selectorKey}";
        if (_indexes.TryGetValue(negativeKey, out _)) return null;
        var root = loaded.Index.Find(provision);
        if (root is null)
        {
            _indexes.Set(negativeKey, true, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = _options.NegativeCacheTtl });
            return null;
        }
        var fragment = loaded.Index.Fragments[root.Value];
        if (!fragment.IsEffective && !options.IncludeIneffective)
            throw new ESbirkaException("provision_not_effective", "The requested provision exists but is not effective in the resolved snapshot.");
        var fragments = loaded.Index.Subtree(root.Value, options.IncludeDescendants);
        return new(number, version, loaded.Version, provision, fragment.Citation, fragment.Eli, fragment.StableUrl,
            fragment.IsEffective, string.Join('\n', fragments.Select(item => FragmentIndex.PlainText(item.Xhtml)).Where(text => text.Length > 0)),
            options.IncludeXhtml ? string.Join('\n', fragments.Select(item => item.Xhtml).Where(text => text is not null)) : null,
            fragments.Select(item => item with
            {
                Xhtml = options.IncludeXhtml ? item.Xhtml : null,
                References = options.IncludeReferences ? item.References : [],
                Raw = FilterRaw(item.Raw, options)
            }).ToArray(),
            options.IncludeReferences ? fragments.SelectMany(item => item.References).ToArray() : [], loaded.Freshness);
    }

    public async Task<IReadOnlyList<LawStructureNode>> GetStructureAsync(string publicationNumber,
        LawVersionSelector version, CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAsync(LawAddress.NormalizePublicationNumber(publicationNumber), version, RefreshPolicy.PreferCache, false, cancellationToken);
        var children = new Dictionary<int, List<int>>();
        for (var index = 0; index < loaded.Index.Fragments.Count; index++)
        {
            if (!LawAddress.IsAddressable(loaded.Index.Fragments[index].Eli)) continue;
            var parent = loaded.Index.Parents[index];
            while (parent is { } ancestor && !LawAddress.IsAddressable(loaded.Index.Fragments[ancestor].Eli)) parent = loaded.Index.Parents[ancestor];
            if (!children.TryGetValue(parent ?? -1, out var group)) children[parent ?? -1] = group = [];
            group.Add(index);
        }
        IReadOnlyList<LawStructureNode> Build(int parent) => !children.TryGetValue(parent, out var group) ? [] : group.Select(index =>
        {
            var fragment = loaded.Index.Fragments[index];
            var title = loaded.Index.Fragments.Skip(index + 1).TakeWhile(item => item.Depth > fragment.Depth)
                .FirstOrDefault(item => item.Depth == fragment.Depth + 1 && item.Type is "Nadpis_nad" or "Nadpis_pod");
            return new LawStructureNode(LawAddress.SelectorFromEli(fragment.Eli), fragment.ShortCitation,
                FragmentIndex.PlainText(title?.Xhtml), fragment.Eli, fragment.Type, fragment.IsEffective,
                Build(index), version, loaded.Version, loaded.Freshness);
        }).ToArray();
        return Build(-1);
    }

    public async Task<IReadOnlyList<LawVersion>> GetVersionsAsync(string publicationNumber, CancellationToken cancellationToken = default)
    {
        var number = LawAddress.NormalizePublicationNumber(publicationNumber);
        var gate = _coordinator.ForDocument(number);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var key = "versions:" + number;
            var entry = await _cache.ReadAsync(key, cancellationToken);
            var hit = entry is not null && _time.GetUtcNow() - entry.LastValidatedAt < _options.VersionListTtl;
            if (!hit)
            {
                var path = LawAddress.DocumentPath(number, new());
                var response = await FetchAsync(path, "/zneni-ke-srovnani", false, cancellationToken);
                ParseVersions(response.Content, number);
                entry = new(key, number, "versions", null, response.Content, _time.GetUtcNow(), _time.GetUtcNow());
                await _cache.WriteBatchAsync([entry], cancellationToken);
            }
            var freshness = new CacheFreshness(hit ? "hit" : "miss", entry!.FetchedAt, entry.LastValidatedAt);
            return ParseVersions(entry.Payload, number).Select(version => version with { Cache = freshness }).ToArray();
        }
        finally { gate.Release(); }
    }

    public async Task<LawSearchResult> SearchAsync(string query, int page = 0, int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 2000 || pageSize is < 1 or > 100
            || page < 0 || page > (int.MaxValue / pageSize) - 1)
            throw new ESbirkaException("invalid_argument", "query must contain 1-2000 characters, page must be a nonnegative page index within the integer result range, and pageSize must be 1-100.");
        query = query.Trim();
        var json = JsonSerializer.Serialize(new { fulltext = query, start = page, pocet = pageSize, razeni = new[] { "+relevance" } });
        var response = await FetchAsync(new Uri(_options.BaseUri, "sbr-cache/jednoducha-vyhledavani"),
            "upstream_unavailable", cancellationToken, json);
        return ParseContract(() =>
        {
            using var document = JsonDocument.Parse(response.Content);
            var total = document.RootElement.GetProperty("pocetCelkem").GetInt64();
            var items = document.RootElement.GetProperty("seznam").EnumerateArray().Select(item =>
            {
                string Required(string name) => item.GetProperty(name).GetString() is { Length: > 0 } value
                    ? value : throw new FormatException($"Missing search result {name}.");
                var path = Required("staleUrl");
                if (!System.Text.RegularExpressions.Regex.IsMatch(path, @"^/[a-z][a-z0-9]*(?:/[A-Za-z0-9_-]+)+$"))
                    throw new FormatException("Search result must have a local document path.");
                var collection = path.Split('/')[1];
                var code = Required("kodDokumentuSbirky");
                string? number = null;
                if (collection == "sb")
                {
                    try { number = LawAddress.NormalizePublicationNumber(code); }
                    catch (ESbirkaException exception) { throw new FormatException("Invalid search publication code.", exception); }
                    var prefix = LawAddress.DocumentPath(number, new());
                    if (path != prefix)
                    {
                        if (!path.StartsWith(prefix + "/", StringComparison.Ordinal))
                            throw new FormatException("Search result path and publication code disagree.");
                        var version = path[(prefix.Length + 1)..];
                        if (version != "0000-00-00" && !DateOnly.TryParseExact(version, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                            throw new FormatException("Invalid search result version path.");
                    }
                }
                DateOnly? published = item.TryGetProperty("datum", out var date) && date.ValueKind != JsonValueKind.Null
                    ? DateOnly.ParseExact(date.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
                return new LawSearchItem(collection, code, number, Required("nazev"), path,
                    Required("stavDokumentuSbirky"), published);
            }).ToArray();
            if (total < 0 || items.Length > pageSize || items.Length > total)
                throw new FormatException("Inconsistent search result count.");
            return new LawSearchResult(query, page, pageSize, total, ((long)page + 1) * pageSize < total, items, _time.GetUtcNow());
        });
    }

    private async Task<LoadedSnapshot> LoadAsync(string number, LawVersionSelector selector, RefreshPolicy policy,
        bool allowStale, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(policy)) throw new ESbirkaException("invalid_argument", "Invalid refresh policy.");
        var requestPath = LawAddress.DocumentPath(number, selector);
        var current = selector.AsOf is null && !selector.AsPromulgated;
        var aliasKey = current ? "current:" + number : "resolved:" + requestPath;
        var gate = _coordinator.ForDocument(number);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var alias = await _cache.ReadAsync(aliasKey, cancellationToken);
            var snapshot = alias?.CanonicalPath is { } path ? await _cache.ReadAsync("snapshot:" + path, cancellationToken) : null;
            if (snapshot is null && !current)
                snapshot = await _cache.ReadAsync("snapshot:" + requestPath, cancellationToken);
            var validated = alias?.LastValidatedAt ?? snapshot?.LastValidatedAt ?? DateTimeOffset.MinValue;
            var age = _time.GetUtcNow() - validated;
            var mutableResolution = current || (selector.AsOf is { } date && date >= DateOnly.FromDateTime(validated.UtcDateTime));
            var fresh = !mutableResolution || age < _options.CurrentAliasTtl;
            if (snapshot is not null && policy != RefreshPolicy.Revalidate && fresh)
                return Decode(snapshot, validated, "hit");
            if (policy == RefreshPolicy.CacheOnly)
            {
                if (snapshot is not null && allowStale && age <= _options.MaximumStaleAge)
                    return Decode(snapshot, validated, "stale", true);
                throw new ESbirkaException("cache_miss", "No sufficiently fresh complete snapshot is cached for this request.");
            }
            try
            {
                var metadata = await FetchAsync(requestPath, "", !current, cancellationToken);
                var version = ParseMetadata(metadata.Content, number);
                ValidateResolution(selector, version);
                var canonicalKey = "snapshot:" + version.StableUrl;
                var existing = await _cache.ReadAsync(canonicalKey, cancellationToken);
                var now = _time.GetUtcNow();
                ESbirkaCacheEntry completed;
                if (existing is null)
                {
                    var pages = new List<ESbirkaFetchResponse>();
                    var fragments = new List<LawFragment>();
                    var expectedCount = 1;
                    var size = 0;
                    for (var page = 0; page < expectedCount; page++)
                    {
                        var response = await FetchAsync(version.StableUrl, $"/fragmenty?cisloStranky={page}", true, cancellationToken);
                        var parsed = FragmentIndex.ParsePage(response.Content, version.StableUrl, out var count);
                        if ((page > 0 && count != expectedCount) || count > _options.MaximumPages || parsed.Count == 0)
                            throw new ESbirkaException("upstream_contract_changed", "Inconsistent page count or empty fragment page.");
                        if (page == 0) expectedCount = count;
                        size += Encoding.UTF8.GetByteCount(response.Content);
                        if (size > _options.MaximumSnapshotBytes) throw new ESbirkaException("response_too_large", "Complete snapshot exceeds configured limit.");
                        pages.Add(response);
                        fragments.AddRange(parsed);
                    }
                    _ = new FragmentIndex(fragments);
                    completed = new(canonicalKey, number, "snapshot", version.StableUrl,
                        JsonSerializer.Serialize(new RawSnapshot(version, metadata, pages)), now, now);
                }
                else
                {
                    var raw = Deserialize(existing);
                    completed = existing with { Payload = JsonSerializer.Serialize(raw with { Version = version, Metadata = metadata }), LastValidatedAt = now };
                }
                var newAlias = new ESbirkaCacheEntry(aliasKey, number, current ? "current" : "resolved", version.StableUrl, "{}", now, now);
                var loaded = Decode(completed, now, snapshot is null ? "miss" : "revalidated");
                await _cache.WriteBatchAsync([completed, newAlias], cancellationToken);
                _logger.LogInformation("Resolved {Publication} {RequestedPath}: {PreviousVersion} -> {CanonicalVersion}", number, requestPath, alias?.CanonicalPath, version.StableUrl);
                return loaded;
            }
            catch (ESbirkaException exception) when (exception.Code == "upstream_unavailable" && snapshot is not null && allowStale && age <= _options.MaximumStaleAge)
            {
                ESbirkaMetrics.RefreshFailures.Add(1);
                _logger.LogWarning(exception, "Serving explicitly permitted stale snapshot for {Publication}", number);
                return Decode(snapshot, validated, "stale", true);
            }
        }
        finally { gate.Release(); }
    }

    private LoadedSnapshot Decode(ESbirkaCacheEntry entry, DateTimeOffset validated, string status, bool stale = false)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entry.Payload)));
        var key = $"index:{FragmentIndex.SchemaVersion}:{entry.CanonicalPath}:{hash}";
        var raw = Deserialize(entry);
        if (!_indexes.TryGetValue(key, out FragmentIndex? index))
        {
            var fragments = new List<LawFragment>();
            foreach (var page in raw.Pages)
            {
                fragments.AddRange(FragmentIndex.ParsePage(page.Content, raw.Version.StableUrl, out var count));
                if (count != raw.Pages.Count) throw new ESbirkaException("cache_corrupt", "Cached snapshot has missing pages.");
            }
            index = new(fragments);
            _indexes.Set(key, index, new MemoryCacheEntryOptions { Size = Math.Max(1, Encoding.UTF8.GetByteCount(entry.Payload) * 3L), SlidingExpiration = TimeSpan.FromMinutes(20) });
        }
        var freshness = new CacheFreshness(status, entry.FetchedAt, validated, stale);
        return new(index!, raw.Version with { Cache = freshness }, freshness);
    }

    private static JsonElement FilterRaw(JsonElement raw, ProvisionOptions options)
    {
        var value = JsonNode.Parse(raw.GetRawText())!.AsObject();
        if (!options.IncludeXhtml) value.Remove("xhtml");
        if (!options.IncludeReferences) value.Remove("odkazyZFragmentu");
        return JsonSerializer.SerializeToElement(value);
    }

    private static RawSnapshot Deserialize(ESbirkaCacheEntry entry)
    {
        try { return JsonSerializer.Deserialize<RawSnapshot>(entry.Payload) ?? throw new JsonException("Empty snapshot."); }
        catch (JsonException exception) { throw new ESbirkaException("cache_corrupt", "Unable to read cached snapshot; purge the affected version.", exception); }
    }

    private Task<ESbirkaFetchResponse> FetchAsync(string path, string suffix, bool versioned, CancellationToken cancellationToken) =>
        FetchAsync(LawAddress.Endpoint(_options.BaseUri, path, suffix), versioned ? "version_not_available" : "law_not_found", cancellationToken);

    private async Task<ESbirkaFetchResponse> FetchAsync(Uri uri, string notFoundCode, CancellationToken cancellationToken, string? json = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_options.RequestTimeout);
                var response = await _coordinator.FetchAsync(() => json is null
                    ? _fetcher.FetchAsync(uri, ESbirkaContentKind.Json, timeout.Token)
                    : _fetcher.PostJsonAsync(uri, json, timeout.Token), _options.RequestDelay, timeout.Token);
                if (response.StatusCode is 400 or 404)
                    throw new ESbirkaException(json is not null && response.StatusCode == 400 ? "invalid_argument" : notFoundCode,
                        $"e-Sbirka returned HTTP {response.StatusCode} for {uri.AbsolutePath}.");
                if (response.StatusCode == 429 || response.StatusCode >= 500)
                    throw new HttpRequestException($"Upstream returned HTTP {response.StatusCode}.");
                if (response.StatusCode is < 200 or >= 300)
                    throw new ESbirkaException("upstream_unavailable", $"Upstream returned HTTP {response.StatusCode}.");
                var size = Encoding.UTF8.GetByteCount(response.Content);
                if (size > _options.MaximumResponseBytes) throw new ESbirkaException("response_too_large", "Response exceeds configured limit.");
                ESbirkaMetrics.RemoteBytes.Add(size);
                return response;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is HttpRequestException or OperationCanceledException or IOException)
            {
                if (attempt >= _options.RetryCount)
                    throw new ESbirkaException("upstream_unavailable", "Unable to retrieve e-Sbirka content after bounded retries.", exception);
                await Task.Delay(TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt) + Random.Shared.Next(200)), cancellationToken);
            }
        }
    }

    private static LawVersion ParseMetadata(string json, string number) => ParseContract(() =>
    {
        using var document = JsonDocument.Parse(json);
        return ParseVersion(document.RootElement, number, true);
    });

    private static IReadOnlyList<LawVersion> ParseVersions(string json, string number) => ParseContract<IReadOnlyList<LawVersion>>(() =>
    {
        using var document = JsonDocument.Parse(json);
        var versions = document.RootElement.GetProperty("dokumentuSbirkyZneniKeSrovnani").EnumerateArray()
            .Select(item => ParseVersion(item, number, false)).ToArray();
        if (versions.Length == 0 || versions.Select(version => version.StableUrl).Distinct().Count() != versions.Length)
            throw new FormatException("Empty or duplicate version list.");
        return versions;
    });

    private static LawVersion ParseVersion(JsonElement item, string number, bool metadata)
    {
        var path = item.GetProperty("staleUrl").GetString() ?? throw new FormatException("Missing canonical path.");
        var prefix = LawAddress.DocumentPath(number, new()) + "/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) throw new FormatException("Canonical path belongs to a different law.");
        var date = path[prefix.Length..];
        if (date != "0000-00-00" && !DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new FormatException("Canonical path must contain one version date.");
        var from = DateOnly.ParseExact(item.GetProperty("datumUcinnostiZneniOd").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        DateOnly? through = item.TryGetProperty("datumUcinnostiZneniDo", out var end) && end.ValueKind != JsonValueKind.Null
            ? DateOnly.ParseExact(end.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
        var type = item.GetProperty("typZneni").GetString() ?? throw new FormatException("Missing version type.");
        if (through < from || (date == "0000-00-00") != (type == "VYHLASENE")) throw new FormatException("Inconsistent version interval or type.");
        var eli = metadata ? item.GetProperty("eli").GetString() : "/eli/cz" + path;
        if (eli != "/eli/cz" + path) throw new FormatException("Canonical ELI and path disagree.");
        var title = metadata ? item.GetProperty("nazev").GetString() ?? "" : "";
        var amendments = item.TryGetProperty("novely", out var novely) && novely.ValueKind != JsonValueKind.Null
            ? novely.EnumerateArray().Select(amendment => amendment.Clone()).ToArray() : [];
        return new(path, eli, from, through, type, title, amendments);
    }

    private static T ParseContract<T>(Func<T> parse)
    {
        try { return parse(); }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)
        {
            ESbirkaMetrics.ContractFailures.Add(1);
            throw new ESbirkaException("upstream_contract_changed", "Invalid e-Sbirka response: " + exception.Message, exception);
        }
    }

    private static void ValidateResolution(LawVersionSelector selector, LawVersion version)
    {
        if (selector.AsPromulgated != (version.Type == "VYHLASENE"))
            throw new ESbirkaException("upstream_contract_changed", "Requested version mode does not match the resolved snapshot.");
        if (selector.AsOf is { } date && (date < version.EffectiveFrom || date > version.EffectiveThrough))
            throw new ESbirkaException("version_not_available", "Requested date is outside the server-resolved effective interval.");
    }

    public async Task RevalidateCurrentAsync(string publicationNumber, CancellationToken cancellationToken = default) =>
        _ = await LoadAsync(LawAddress.NormalizePublicationNumber(publicationNumber), new(), RefreshPolicy.Revalidate, false, cancellationToken);

    public async Task PurgeCurrentAliasAsync(string publicationNumber, CancellationToken cancellationToken = default) =>
        await PurgeAsync(publicationNumber, records => records.Where(record => record.Kind is "current" or "versions").ToArray(), false, cancellationToken);

    public Task PurgeProvisionAsync(string publicationNumber, LawVersionSelector version, LegalProvisionSelector provision, CancellationToken cancellationToken = default)
    {
        _ = LawAddress.DocumentPath(publicationNumber, version);
        _ = LawAddress.Segments(provision);
        cancellationToken.ThrowIfCancellationRequested();
        _indexes.Clear();
        _logger.LogInformation("Purged derived indexes and negative lookups for {Publication}; raw snapshots retained", publicationNumber);
        return Task.CompletedTask;
    }

    public async Task PurgeVersionAsync(string publicationNumber, DateOnly effectiveFrom, CancellationToken cancellationToken = default)
    {
        var path = LawAddress.DocumentPath(publicationNumber, new(effectiveFrom));
        await PurgeAsync(publicationNumber, records => records.Where(record => record.CanonicalPath == path || record.Kind == "versions").ToArray(), false, cancellationToken);
    }

    public Task<CachePurgeResult> PurgeDocumentAsync(string publicationNumber, bool includeHistoricalVersions,
        bool dryRun = false, CancellationToken cancellationToken = default) => PurgeAsync(publicationNumber, records =>
    {
        var current = records.FirstOrDefault(record => record.Kind == "current")?.CanonicalPath;
        return records.Where(record => includeHistoricalVersions || record.Kind is "current" or "versions" || (current is not null && record.CanonicalPath == current)).ToArray();
    }, dryRun, cancellationToken);

    private async Task<CachePurgeResult> PurgeAsync(string publicationNumber,
        Func<IReadOnlyList<ESbirkaCacheRecord>, IReadOnlyList<ESbirkaCacheRecord>> select, bool dryRun, CancellationToken cancellationToken)
    {
        var number = LawAddress.NormalizePublicationNumber(publicationNumber);
        var gate = _coordinator.ForDocument(number);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var records = select(await _cache.ListAsync(number, cancellationToken));
            if (!dryRun)
            {
                await _cache.DeleteAsync(records.Select(record => record.Key).ToArray(), cancellationToken);
                _indexes.Clear();
            }
            var result = new CachePurgeResult(records.Count, records.Sum(record => record.Bytes), dryRun);
            _logger.LogInformation("Cache purge {Publication}: {Records} records, {Bytes} bytes, dry run {DryRun}", number, result.Records, result.Bytes, dryRun);
            return result;
        }
        finally { gate.Release(); }
    }

    public void Dispose() => _indexes.Dispose();
}