using System.Text.Json;

namespace ESbirka.Client;

public sealed record LawVersionSelector(DateOnly? AsOf = null, bool AsPromulgated = false);

public sealed record LegalProvisionSelector(
    string? Part = null, string? Chapter = null, string? Division = null,
    string? SectionDivision = null, string? Subdivision = null, string? Section = null,
    string? Paragraph = null, string? Letter = null, string? Point = null);

public enum RefreshPolicy { PreferCache, Revalidate, CacheOnly }

public sealed record ProvisionOptions(
    bool IncludeDescendants = true, bool IncludeXhtml = false, bool IncludeReferences = true,
    bool IncludeIneffective = false, RefreshPolicy RefreshPolicy = RefreshPolicy.PreferCache,
    bool AllowStale = false);

public sealed record CacheFreshness(string Status, DateTimeOffset FetchedAt,
    DateTimeOffset LastValidatedAt, bool IsStale = false);

public sealed record LawVersion(string StableUrl, string Eli, DateOnly EffectiveFrom,
    DateOnly? EffectiveThrough, string Type, string Title, IReadOnlyList<JsonElement> Amendments,
    CacheFreshness? Cache = null);

public sealed record LawReference(string Type, JsonElement Target, JsonElement Raw);

public sealed record LawSearchItem(string Collection, string PublicationCode, string? PublicationNumber,
    string Title, string StableUrl, string Status, DateOnly? PublishedOn);

public sealed record LawSearchResult(string Query, int Page, int PageSize, long TotalCount,
    bool HasMore, IReadOnlyList<LawSearchItem> Items, DateTimeOffset RetrievedAt);

public sealed record LawFragment(long Id, string Eli, string StableUrl, string Type, int Depth,
    string Citation, string ShortCitation, bool IsEffective, string? Xhtml,
    IReadOnlyList<LawReference> References, JsonElement Raw);

public sealed record LawProvision(string PublicationNumber, LawVersionSelector RequestedVersion,
    LawVersion ResolvedVersion, LegalProvisionSelector Selector, string Citation, string Eli,
    string StableUrl, bool IsEffective, string PlainText, string? Xhtml,
    IReadOnlyList<LawFragment> Fragments, IReadOnlyList<LawReference> References, CacheFreshness Cache);

public sealed record LawStructureNode(LegalProvisionSelector Selector, string Label, string Title,
    string Eli, string Type, bool IsEffective, IReadOnlyList<LawStructureNode> Children,
    LawVersionSelector RequestedVersion, LawVersion ResolvedVersion, CacheFreshness Cache);

public sealed class ESbirkaException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}

public interface IESbirkaClient
{
    Task<LawProvision?> GetProvisionAsync(string publicationNumber, LawVersionSelector version,
        LegalProvisionSelector provision, ProvisionOptions? options = null,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LawStructureNode>> GetStructureAsync(string publicationNumber,
        LawVersionSelector version, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LawVersion>> GetVersionsAsync(string publicationNumber,
        CancellationToken cancellationToken = default);
    Task<LawSearchResult> SearchAsync(string query, int page = 0, int pageSize = 20,
        CancellationToken cancellationToken = default) =>
        throw new ESbirkaException("search_not_supported", "This client does not support search.");
}