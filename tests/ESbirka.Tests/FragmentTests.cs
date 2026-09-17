using System.Text.Json;
using ESbirka.Client;

namespace ESbirka.Tests;

public class FragmentTests
{
    [Fact]
    public void EncodedPathRemainsOneSegment()
    {
        var path = LawAddress.DocumentPath(" 65 / 2022 Sb. ", new(new(2023, 5, 15)));
        var uri = LawAddress.Endpoint(new("https://e-sbirka.gov.cz"), path, "/fragmenty?cisloStranky=0");
        Assert.Equal("https://e-sbirka.gov.cz/sbr-cache/dokumenty-sbirky/%2Fsb%2F2022%2F65%2F2023-05-15/fragmenty?cisloStranky=0", uri.AbsoluteUri);
        Assert.Equal(["par_7aa", "odst_1", "pism_a"], LawAddress.Segments(new(Section: "\u00a7 7AA", Paragraph: "odst. 1", Letter: "p\u00edsm. a)")));
    }

    [Fact]
    public void SubtreeContinuesAcrossPagesAndStopsAtSibling()
    {
        var fragments = new[] { Fragment(1, 2, "/par_310"), Fragment(2, 3, "/par_310/pism_b"),
            Fragment(3, 3, "/par_310/pism_c"), Fragment(4, 2, "/par_311") };
        var index = new FragmentIndex(fragments);
        Assert.Equal(0, index.Find(new(Section: "310")));
        Assert.Equal([1L, 2L, 3L], index.Subtree(0).Select(fragment => fragment.Id));
        Assert.Equal(new int?[] { null, 0, 0, null }, index.Parents);
    }

    [Fact]
    public void PartialChapterMustNotSilentlyChooseFirstMatch()
    {
        var index = new FragmentIndex([Fragment(1, 1, "/cast_1/hlava_2"), Fragment(2, 1, "/cast_2/hlava_2")]);
        Assert.Equal("ambiguous_selector", Assert.Throws<ESbirkaException>(() => index.Find(new(Chapter: "2"))).Code);
        Assert.Equal(1, index.Find(new(Part: "2", Chapter: "2")));
    }

    [Fact]
    public void HtmlParserPreservesWordAndBlockBoundariesWithoutScripts()
    {
        Assert.Equal("a) One & two\nNext", FragmentIndex.PlainText("<p><var>a)</var> One &amp; two</p><div>Next</div><script>bad()</script>"));
    }

    [Fact]
    public void VirtualDocumentRootIsValidButNeighboringPathIsNot()
    {
        const string path = "/sb/2022/65/2025-09-03";
        var json = JsonSerializer.Serialize(new { pocetStranek = 1, seznam = new[] { new
        {
            id = 9619465, eli = "/eli/cz" + path + "/dokument", staleUrl = path,
            kodTypuFragmentu = "Virtual_Document", hloubka = 0, uplnaCitace = "Law", zkracenaCitace = "Law",
            jeUcinny = true, odkazyZFragmentu = Array.Empty<object>()
        } } });
        var fragments = FragmentIndex.ParsePage(json, path, out var count);
        Assert.Equal(1, count);
        Assert.Null(Assert.Single(fragments).Xhtml);
        Assert.Throws<ESbirkaException>(() => FragmentIndex.ParsePage(json.Replace("/dokument", "/dokumentation"), path, out _));
    }

    internal static LawFragment Fragment(long id, int depth, string suffix) => new(id,
        "/eli/cz/sb/2022/65/2025-09-03/dokument/norma" + suffix, "/sb/2022/65/2025-09-03#" + suffix,
        "UnknownPreservedType", depth, "citation", "label", true, "<p>text</p>", [], JsonSerializer.SerializeToElement(new { id }));
}