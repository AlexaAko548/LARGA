using LARGA.ManagerWeb.Services.Faq;

namespace LARGA.Tests.Help;

/// <summary>Help &amp; FAQs: the search box, the category chips, and the content's own consistency.</summary>
public class FaqSearchTests
{
    private static readonly FaqEntry[] Sample =
    [
        new("a", "ledger", "How is the amount due computed?", "Boundary plus late fee.", Keywords: "price"),
        new("b", "ledger", "Why was a GCash receipt rejected?", "The reference number was already used."),
        new("c", "fuel", "Why was a fuel submission flagged?", "The station isn't Petron or Shell."),
    ];

    [Fact]
    public void Empty_search_shows_everything()
    {
        Assert.Equal(3, FaqSearch.Filter(Sample, null).Count);
        Assert.Equal(3, FaqSearch.Filter(Sample, "   ").Count);
    }

    [Fact]
    public void Search_ignores_case_and_looks_in_question_answer_and_keywords()
    {
        Assert.Equal(["a"], FaqSearch.Filter(Sample, "COMPUTED").Select(e => e.Id));
        Assert.Equal(["c"], FaqSearch.Filter(Sample, "petron").Select(e => e.Id));
        Assert.Equal(["a"], FaqSearch.Filter(Sample, "price").Select(e => e.Id));
    }

    [Fact]
    public void Every_word_must_match_in_any_order()
    {
        Assert.Equal(["b"], FaqSearch.Filter(Sample, "receipt gcash").Select(e => e.Id));
        Assert.Empty(FaqSearch.Filter(Sample, "gcash petron"));
    }

    [Fact]
    public void Category_chip_narrows_the_results_and_combines_with_search()
    {
        Assert.Equal(["a", "b"], FaqSearch.Filter(Sample, null, "ledger").Select(e => e.Id));
        Assert.Equal(["b"], FaqSearch.Filter(Sample, "rejected", "ledger").Select(e => e.Id));
        Assert.Empty(FaqSearch.Filter(Sample, "rejected", "fuel"));
        Assert.Equal(3, FaqSearch.Filter(Sample, null, "").Count);
    }

    [Fact]
    public void Real_content_search_finds_the_boundary_answer()
    {
        var hits = FaqSearch.Filter(FaqCatalog.Entries, "800");
        Assert.Contains(hits, e => e.Id == "amount-due");
    }

    [Fact]
    public void Content_has_unique_ids_and_every_entry_belongs_to_a_known_category()
    {
        Assert.Equal(FaqCatalog.Entries.Count, FaqCatalog.Entries.Select(e => e.Id).Distinct().Count());
        Assert.Equal(FaqCatalog.Categories.Count, FaqCatalog.Categories.Select(c => c.Id).Distinct().Count());
        Assert.All(FaqCatalog.Entries, e => Assert.Contains(FaqCatalog.Categories, c => c.Id == e.CategoryId));
        Assert.All(FaqCatalog.Categories, c => Assert.Contains(FaqCatalog.Entries, e => e.CategoryId == c.Id));
    }
}
