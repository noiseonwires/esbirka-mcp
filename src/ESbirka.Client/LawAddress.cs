using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ESbirka.Client;

public static class LawAddress
{
    private static readonly string[] Prefixes =
        ["cast_", "hlava_", "dil_", "oddil_", "pododdil_", "par_", "odst_", "pism_", "bod_"];
    private static readonly string[] Labels =
        ["cast", "hlava", "dil", "oddil", "pododdil", "\u00a7|par\\.?", "odst\\.?", "pism\\.?", "bod"];

    public static string NormalizePublicationNumber(string value)
    {
        var match = Regex.Match(value.Trim(), @"^(?<number>[1-9][0-9]*)\s*/\s*(?<year>[0-9]{4})(?:\s*Sb\.?)?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
            throw new ESbirkaException("invalid_argument", "Publication number must be number/year, optionally followed by Sb.");
        return $"{match.Groups["number"].Value}/{match.Groups["year"].Value}";
    }

    public static string DocumentPath(string publicationNumber, LawVersionSelector version)
    {
        if (version.AsOf is not null && version.AsPromulgated)
            throw new ESbirkaException("invalid_argument", "asOf and promulgated mode are mutually exclusive.");
        var parts = NormalizePublicationNumber(publicationNumber).Split('/');
        var path = $"/sb/{parts[1]}/{parts[0]}";
        return version.AsPromulgated ? path + "/0000-00-00"
            : version.AsOf is { } date ? path + "/" + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : path;
    }

    public static Uri Endpoint(Uri baseUri, string documentPath, string suffix = "") =>
        new(baseUri, "sbr-cache/dokumenty-sbirky/" + Uri.EscapeDataString(documentPath) + suffix);

    public static IReadOnlyList<string> Segments(LegalProvisionSelector selector)
    {
        string?[] values = [selector.Part, selector.Chapter, selector.Division, selector.SectionDivision,
            selector.Subdivision, selector.Section, selector.Paragraph, selector.Letter, selector.Point];
        var result = new List<string>();
        for (var index = 0; index < values.Length; index++)
        {
            if (values[index] is not { } value) continue;
            var normalized = RemoveDiacritics(value.Trim().ToLowerInvariant());
            normalized = Regex.Replace(normalized, "^(?:" + Labels[index] + @")\s*", "");
            normalized = normalized.Trim().Trim('(', ')', '.').Trim();
            if (!Regex.IsMatch(normalized, index == 7 ? "^[a-z]+$" : "^[0-9]+[a-z]*$"))
                throw new ESbirkaException("invalid_argument", $"Invalid provision component: {value}.");
            result.Add(Prefixes[index] + normalized);
        }
        if (result.Count == 0)
            throw new ESbirkaException("invalid_argument", "At least one provision component is required.");
        return result;
    }

    public static string SemanticPath(string eli)
    {
        const string marker = "/dokument/norma";
        var position = eli.IndexOf(marker, StringComparison.Ordinal);
        return position < 0 ? "" : eli[(position + marker.Length)..].TrimEnd('/');
    }

    public static LegalProvisionSelector SelectorFromEli(string eli)
    {
        var segments = SemanticPath(eli).Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? Value(string prefix) => segments.LastOrDefault(segment => segment.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
        return new(Value("cast_"), Value("hlava_"), Value("dil_"), Value("oddil_"), Value("pododdil_"),
            Value("par_"), Value("odst_"), Value("pism_"), Value("bod_"));
    }

    public static bool IsAddressable(string eli) => Prefixes.Any(prefix =>
        SemanticPath(eli).Split('/').Last().StartsWith(prefix, StringComparison.Ordinal));

    private static string RemoveDiacritics(string value) => string.Concat(value.Normalize(NormalizationForm.FormD)
        .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark))
        .Normalize(NormalizationForm.FormC);
}