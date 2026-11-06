using System.Text.RegularExpressions;
using LARGA.ManagerWeb.Services.Faq;
using LARGA.ManagerWeb.Services.Tours;

namespace LARGA.Tests.Help;

/// <summary>Page tours: role filtering, the FAQ link on the last step, and that every tour points at real data-tour markers.</summary>
public class TourTests
{
    private static readonly PageTour Sample = new("sample", 2, "ledger",
    [
        new TourStep("a", "First", "Everyone sees this."),
        new TourStep("b", "Reports", "Create a report.", ManagerOnly: true),
        new TourStep("c", "Last real step", "Still for everyone."),
    ]);

    [Fact]
    public void Manager_gets_every_step_pointing_at_its_element()
    {
        var steps = TourService.Resolve(Sample, isManager: true);

        Assert.Equal(["a", "b", "c", null], steps.Select(s => s.Target));
        Assert.Equal("Create a report.", steps[1].Body);
    }

    [Fact]
    public void Assistant_manager_gets_a_note_instead_of_the_manager_only_step()
    {
        var steps = TourService.Resolve(Sample, isManager: false);

        Assert.Equal(["a", null, "c", null], steps.Select(s => s.Target));
        Assert.Equal(TourService.ManagerOnlyNote, steps[1].Body);
        Assert.Contains("Manager only", steps[1].Title);
        Assert.DoesNotContain("Create a report", steps[1].Body);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Last_step_links_to_the_pages_faq_category(bool isManager)
    {
        var steps = TourService.Resolve(Sample, isManager);

        Assert.Equal("/help#ledger", steps[^1].FaqHref);
        Assert.Contains("More questions? Open Help & FAQs", steps[^1].Body);
        Assert.All(steps.Take(steps.Count - 1), s => Assert.Null(s.FaqHref));
    }

    [Fact]
    public void Seen_key_changes_with_user_tour_and_version()
    {
        Assert.Equal("u1:sample:v2", TourService.SeenKey("u1", Sample));
        Assert.NotEqual(TourService.SeenKey("u1", Sample), TourService.SeenKey("u2", Sample));
        Assert.NotEqual(TourService.SeenKey("u1", Sample), TourService.SeenKey("u1", Sample with { Version = 3 }));
    }

    [Theory]
    [InlineData("garage", "garage")]
    [InlineData("/garage/", "garage")]
    [InlineData("garage?tab=1", "garage")]
    [InlineData("garage#section-pending-reports", "garage")]
    [InlineData("driver-shifts", "driver-shifts")]
    [InlineData("", "")]
    public void Route_comes_from_the_first_part_of_the_address(string path, string expected) =>
        Assert.Equal(expected, TourCatalog.RouteOf(path));

    [Fact]
    public void Pages_without_a_tour_find_none_and_a_tab_key_wins_over_the_route()
    {
        Assert.Null(TourCatalog.Find(null, "help"));
        Assert.Null(TourCatalog.Find(null, "login"));
        Assert.Equal("driver-shifts-logs", TourCatalog.Find("driver-shifts-logs", "driver-shifts")!.Id);
        Assert.Equal("driver-shifts", TourCatalog.Find(null, "driver-shifts")!.Id);
    }

    [Fact]
    public void Every_tour_has_four_to_six_steps_including_the_faq_step()
    {
        foreach (PageTour tour in TourCatalog.All)
        {
            int total = TourService.Resolve(tour, isManager: true).Count;
            Assert.InRange(total, 4, 6);
        }
    }

    [Fact]
    public void Tour_ids_are_unique_and_their_faq_category_exists()
    {
        Assert.Equal(TourCatalog.All.Count, TourCatalog.All.Select(t => t.Id).Distinct().Count());
        foreach (PageTour tour in TourCatalog.All)
        {
            Assert.Contains(FaqCatalog.Categories, c => c.Id == tour.FaqCategory);
        }
    }

    [Fact]
    public void Every_tour_target_is_marked_on_a_page_with_a_data_tour_attribute()
    {
        string components = Path.Combine(FindRepoRoot(), "LARGA.ManagerWeb", "Components");
        var marked = Directory.EnumerateFiles(components, "*.razor", SearchOption.AllDirectories)
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), "data-tour=\"([a-z0-9-]+)\"").Select(m => m.Groups[1].Value))
            .ToHashSet();

        foreach (string target in TourCatalog.All.SelectMany(t => t.Steps).Select(s => s.Target))
        {
            Assert.Contains(target, marked);
        }
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "LARGA.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new DirectoryNotFoundException("LARGA.sln not found above " + AppContext.BaseDirectory);
    }
}
