using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace ESbirka.Client;

public sealed class FragmentIndex
{
    public const int SchemaVersion = 1;
    public IReadOnlyList<LawFragment> Fragments { get; }
    public IReadOnlyList<int?> Parents { get; }

    public FragmentIndex(IReadOnlyList<LawFragment> fragments)
    {
        var parents = new List<int?>();
        var stack = new Stack<int>();
        var ids = new HashSet<long>();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < fragments.Count; index++)
        {
            var fragment = fragments[index];
            if (fragment.Depth is < 0 or > 128 || !ids.Add(fragment.Id) || !paths.Add(fragment.Eli))
                throw new ESbirkaException("upstream_contract_changed", "Duplicate fragment identity or invalid hierarchy depth.");
            while (stack.Count > 0 && fragments[stack.Peek()].Depth >= fragment.Depth) stack.Pop();
            parents.Add(stack.Count == 0 ? null : stack.Peek());
            stack.Push(index);
        }
        Fragments = fragments;
        Parents = parents;
    }

    public int? Find(LegalProvisionSelector selector)
    {
        var wanted = LawAddress.Segments(selector);
        var matches = new List<int>();
        for (var index = 0; index < Fragments.Count; index++)
        {
            var segments = LawAddress.SemanticPath(Fragments[index].Eli).Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.LastOrDefault() != wanted[^1]) continue;
            var position = 0;
            foreach (var segment in segments)
                if (position < wanted.Count && segment == wanted[position]) position++;
            if (position == wanted.Count) matches.Add(index);
        }
        if (matches.Count > 1)
            throw new ESbirkaException("ambiguous_selector", "Selector matches multiple provisions; specify its enclosing part or chapter.");
        return matches.Count == 0 ? null : matches[0];
    }

    public IReadOnlyList<LawFragment> Subtree(int root, bool includeDescendants = true)
    {
        var end = root + 1;
        while (includeDescendants && end < Fragments.Count && Fragments[end].Depth > Fragments[root].Depth) end++;
        return Fragments.Skip(root).Take(end - root).ToArray();
    }

    public static string PlainText(string? xhtml)
    {
        if (string.IsNullOrEmpty(xhtml)) return "";
        var document = new HtmlDocument();
        document.LoadHtml(xhtml);
        var text = new StringBuilder();
        void Visit(HtmlNode node)
        {
            if (node.Name is "script" or "style" or "template") return;
            if (node.NodeType == HtmlNodeType.Text) text.Append(HtmlEntity.DeEntitize(node.InnerText));
            else
            {
                var block = node.Name is "p" or "div" or "br" or "li" or "tr" or "h1" or "h2" or "h3";
                if (block) text.Append('\n');
                foreach (var child in node.ChildNodes) Visit(child);
                if (block) text.Append('\n');
                if (node.Name is "td" or "th") text.Append(' ');
            }
        }
        Visit(document.DocumentNode);
        return string.Join('\n', text.ToString().Split('\n').Select(line => Regex.Replace(line, @"\s+", " ").Trim())
            .Where(line => line.Length > 0));
    }

    public static IReadOnlyList<LawFragment> ParsePage(string json, string canonicalPath, out int pageCount)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            pageCount = root.GetProperty("pocetStranek").GetInt32();
            if (pageCount < 1 || pageCount > 1000) throw new FormatException("Invalid page count.");
            var expectedRoot = "/eli/cz" + canonicalPath + "/dokument";
            var fragments = new List<LawFragment>();
            foreach (var item in root.GetProperty("seznam").EnumerateArray())
            {
                string Required(string name) => item.GetProperty(name).GetString() is { Length: > 0 } value
                    ? value : throw new FormatException($"Missing {name}.");
                var eli = Required("eli");
                if (eli != expectedRoot && !eli.StartsWith(expectedRoot + "/", StringComparison.Ordinal))
                    throw new FormatException("Fragment belongs to a different canonical version.");
                var references = item.GetProperty("odkazyZFragmentu").EnumerateArray().Select(reference =>
                    new LawReference(reference.GetProperty("kodTypuVazbyOdkazu").GetString() ?? "",
                        reference.GetProperty("cil").Clone(), reference.Clone())).ToArray();
                fragments.Add(new(item.GetProperty("id").GetInt64(), eli, Required("staleUrl"),
                    Required("kodTypuFragmentu"), item.GetProperty("hloubka").GetInt32(),
                    Required("uplnaCitace"), Required("zkracenaCitace"), item.GetProperty("jeUcinny").GetBoolean(),
                    item.TryGetProperty("xhtml", out var xhtml) ? xhtml.GetString() : null, references, item.Clone()));
            }
            return fragments;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new ESbirkaException("upstream_contract_changed", "Invalid fragment page: " + exception.Message, exception);
        }
    }
}