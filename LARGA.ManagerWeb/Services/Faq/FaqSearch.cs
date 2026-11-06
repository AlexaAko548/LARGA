namespace LARGA.ManagerWeb.Services.Faq;

/// <summary>The Help &amp; FAQs search box and category chips.</summary>
public static class FaqSearch
{
    /// <summary>
    /// The questions to show. Every word typed must appear somewhere in a question's text
    /// (question, answer or keywords), in any order and any case. An empty search matches all.
    /// <paramref name="categoryId"/> (null or empty = all) narrows to one category.
    /// </summary>
    public static IReadOnlyList<FaqEntry> Filter(IEnumerable<FaqEntry> entries, string? query, string? categoryId = null)
    {
        string[] words = (query ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return entries
            .Where(e => string.IsNullOrEmpty(categoryId) || e.CategoryId == categoryId)
            .Where(e =>
            {
                string text = $"{e.Question} {e.Answer} {e.Keywords}";
                return words.All(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
            })
            .ToList();
    }
}
