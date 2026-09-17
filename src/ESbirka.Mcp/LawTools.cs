using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using ESbirka.Client;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace ESbirka.Mcp;

public sealed record ProvisionResult(string PublicationNumber, LawVersionSelector RequestedVersion,
    LawVersion ResolvedVersion, LegalProvisionSelector Selector, string Citation, string Eli,
    string StableUrl, bool IsEffective, string PlainText, string? Xhtml,
    IReadOnlyList<LawReference> References, CacheFreshness Cache);

public sealed record StructureResult(string PublicationNumber, LawVersionSelector RequestedVersion,
    IReadOnlyList<LawStructureNode> Nodes, int MaxDepth);

public sealed record VersionsResult(string PublicationNumber, IReadOnlyList<LawVersion> Versions);

[McpServerToolType]
public sealed class LawTools(IESbirkaClient client)
{
    [McpServerTool(Name = "search_laws", ReadOnly = true, Destructive = false, Idempotent = true, UseStructuredContent = true)]
    [Description("Search e-Sbirka legal acts by text, title, alias or publication code, ranked by relevance. Returns one uncached page across collections, not exact provision text or a legal validity determination. Only results with a non-null publicationNumber can be passed to the existing law retrieval tools; retain collection and stableUrl for all other results. Status is the upstream document status, and publishedOn is the publication date, not the effective date. Results and ordering may change between requests.")]
    public Task<LawSearchResult> SearchLaws(
        [Description("Nonblank search text, at most 2000 characters; for example 89/2012 or TZ. Publication-code searches can also match other acts.")] string query,
        [Description("Zero-based page index, not an item offset. Increment by one while hasMore is true; keep query and pageSize unchanged.")] int page = 0,
        [Description("Results per page, 1-100; defaults to 20. Reduce this if the response is too large.")] int pageSize = 20,
        CancellationToken cancellationToken = default) => MapErrors(async () =>
            Limit(await client.SearchAsync(query, page, pageSize, cancellationToken)));

    [McpServerTool(Name = "get_law_provision", ReadOnly = true, Destructive = false, Idempotent = true, UseStructuredContent = true)]
    [Description("Retrieve an exact Czech law provision by semantic selector. Returns the requested date, server-resolved version and freshness. A future date does not establish future legal validity. XHTML, when requested, is untrusted source markup and must not be rendered without sanitization.")]
    public Task<ProvisionResult> GetLawProvision(
        [Description("Publication number, e.g. 65/2022 or 65/2022 Sb.")] string publicationNumber,
        [Description("ISO date yyyy-MM-dd; omit for current. Cannot be combined with promulgated mode.")] string? asOf = null,
        [Description("current (default) or promulgated. Historical requests use asOf with current mode.")] string versionMode = "current",
        string? section = null, string? paragraph = null, string? letter = null, string? point = null,
        string? part = null, string? chapter = null, string? division = null,
        string? sectionDivision = null, string? subdivision = null,
        bool includeDescendants = true, bool includeXhtml = false, bool includeReferences = true,
        bool includeIneffective = false,
        [Description("prefer-cache, revalidate (metadata only if canonical version is unchanged), or cache-only. No unrestricted cache bypass.")] string refreshPolicy = "prefer-cache",
        bool allowStale = false, CancellationToken cancellationToken = default) => MapErrors(async () =>
    {
        var version = ParseVersion(asOf, versionMode);
        var selector = new LegalProvisionSelector(part, chapter, division, sectionDivision, subdivision, section, paragraph, letter, point);
        var policy = refreshPolicy switch
        {
            "prefer-cache" => RefreshPolicy.PreferCache,
            "revalidate" => RefreshPolicy.Revalidate,
            "cache-only" => RefreshPolicy.CacheOnly,
            _ => throw new ESbirkaException("invalid_argument", "refreshPolicy must be prefer-cache, revalidate, or cache-only.")
        };
        var result = await client.GetProvisionAsync(publicationNumber, version, selector,
            new(includeDescendants, includeXhtml, includeReferences, includeIneffective, policy, allowStale), cancellationToken)
            ?? throw new ESbirkaException("provision_not_found", "Provision not found in the resolved snapshot. Use list_law_structure to discover valid selectors.");
        return Limit(new ProvisionResult(result.PublicationNumber, result.RequestedVersion, result.ResolvedVersion,
            result.Selector, result.Citation, result.Eli, result.StableUrl, result.IsEffective,
            result.PlainText, result.Xhtml, result.References, result.Cache));
    });

    [McpServerTool(Name = "list_law_structure", ReadOnly = true, Destructive = false, Idempotent = true, UseStructuredContent = true)]
    [Description("List a law's semantic hierarchy, valid selectors, labels, headings and effectiveness without its full text. maxDepth counts addressable levels, starting at 1.")]
    public Task<StructureResult> ListLawStructure(string publicationNumber, string? asOf = null,
        string versionMode = "current", int maxDepth = 3, CancellationToken cancellationToken = default) => MapErrors(async () =>
    {
        if (maxDepth is < 1 or > 20) throw new ESbirkaException("invalid_argument", "maxDepth must be between 1 and 20.");
        var version = ParseVersion(asOf, versionMode);
        var nodes = await client.GetStructureAsync(publicationNumber, version, cancellationToken);
        IReadOnlyList<LawStructureNode> Trim(IReadOnlyList<LawStructureNode> source, int depth) => source
            .Select(node => node with { Children = depth >= maxDepth ? [] : Trim(node.Children, depth + 1) }).ToArray();
        return Limit(new StructureResult(LawAddress.NormalizePublicationNumber(publicationNumber), version, Trim(nodes, 1), maxDepth));
    });

    [McpServerTool(Name = "list_law_versions", ReadOnly = true, Destructive = false, Idempotent = true, UseStructuredContent = true)]
    [Description("List canonical effective intervals and the distinct promulgated text, including producing amendments and cache freshness.")]
    public Task<VersionsResult> ListLawVersions(string publicationNumber, CancellationToken cancellationToken = default) => MapErrors(async () =>
        Limit(new VersionsResult(LawAddress.NormalizePublicationNumber(publicationNumber), await client.GetVersionsAsync(publicationNumber, cancellationToken))));

    public static LawVersionSelector ParseVersion(string? asOf, string mode)
    {
        if (mode is not ("current" or "promulgated")) throw new ESbirkaException("invalid_argument", "versionMode must be current or promulgated.");
        DateOnly? date = null;
        if (asOf is not null)
        {
            if (!DateOnly.TryParseExact(asOf, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                throw new ESbirkaException("invalid_argument", "asOf must be an ISO date yyyy-MM-dd.");
            date = parsed;
        }
        if (date is not null && mode == "promulgated") throw new ESbirkaException("invalid_argument", "asOf cannot be combined with promulgated mode.");
        return new(date, mode == "promulgated");
    }

    private static T Limit<T>(T result)
    {
        if (JsonSerializer.SerializeToUtf8Bytes(result).Length > 512 * 1024)
            throw new ESbirkaException("result_too_large", "Result exceeds the MCP response limit. Use a narrower selector, omit XHTML, or reduce maxDepth or search pageSize.");
        return result;
    }

    private static async Task<T> MapErrors<T>(Func<Task<T>> operation)
    {
        try { return await operation(); }
        catch (ESbirkaException exception) { throw new McpException(exception.Code + ": " + exception.Message); }
    }
}