using System.Globalization;
using System.Text.RegularExpressions;
using LARGA.SharedCore.Models.Dashboard;

namespace LARGA.ManagerWeb.Components.Shared;

/// <summary>
/// What <see cref="DashboardChart"/> draws - handed as-is to wwwroot/charts.js (Chart.js).
/// Colors are app.css token names ("accent", "success", "warning", "danger", "violet", "muted")
/// so charts follow the light/dark theme; a literal "#rrggbb" also works.
/// </summary>
public sealed class ChartSpec
{
    /// <summary>"bar", "hbar" (horizontal bars) or "doughnut".</summary>
    public string Kind { get; init; } = "bar";
    public List<string> Labels { get; init; } = new();
    public List<ChartSeries> Datasets { get; init; } = new();
    public bool Stacked { get; init; }

    /// <summary>Value axis maximum (e.g. 7 for days in a week); null = fit the data.</summary>
    public double? Max { get; init; }
    public string Prefix { get; init; } = string.Empty;
    public string Suffix { get; init; } = string.Empty;
    public int Decimals { get; init; }

    /// <summary>Optional extra tooltip line per label.</summary>
    public List<string>? Footers { get; init; }

    public bool IsEmpty => Labels.Count == 0 || Datasets.All(d => d.Data.All(v => v <= 0));
}

public sealed class ChartSeries
{
    public string Label { get; init; } = string.Empty;
    public List<double> Data { get; init; } = new();

    /// <summary>Bar charts: one color for the series.</summary>
    public string? Color { get; init; }

    /// <summary>Doughnut: one color per slice (defaults to the palette).</summary>
    public List<string>? Colors { get; init; }
}

/// <summary>The Executive Dashboard's charts, built from its snapshot data.</summary>
public static class DashboardCharts
{
    public static ChartSpec Collections(IReadOnlyList<CollectionDay> days) => new()
    {
        Kind = "bar",
        Stacked = true,
        Prefix = "₱",
        Labels = days.Select(d => d.Date.ToString("ddd d", CultureInfo.InvariantCulture)).ToList(),
        Datasets =
        {
            new ChartSeries { Label = "Collected", Color = "accent", Data = days.Select(d => (double)d.Collected).ToList() },
            new ChartSeries { Label = "Outstanding", Color = "danger", Data = days.Select(d => (double)d.Outstanding).ToList() },
        },
        Footers = days.Select(d => d.Expected <= 0
            ? "Nothing due"
            : $"Due ₱{d.Expected:#,##0} · {d.Shifts} shift{(d.Shifts == 1 ? "" : "s")}").ToList(),
    };

    /// <summary>Days on the road per unit, on a fixed 0-<paramref name="windowDays"/> scale.</summary>
    public static ChartSpec Utilization(IReadOnlyList<ChartPoint>? points, int windowDays) => new()
    {
        Kind = "hbar",
        Max = windowDays,
        Suffix = " d",
        Labels = points?.Select(p => p.Label).ToList() ?? new(),
        Datasets = { new ChartSeries { Label = "Days on the road", Color = "accent", Data = Values(points) } },
    };

    /// <summary>Fuel spend by verification status, in the status colors used across the app.</summary>
    public static ChartSpec FuelByStatus(IReadOnlyList<ChartPoint>? points) => new()
    {
        Kind = "doughnut",
        Prefix = "₱",
        Labels = points?.Select(p => Humanize(p.Label)).ToList() ?? new(),
        Datasets =
        {
            new ChartSeries
            {
                Label = "Fuel spend",
                Data = Values(points),
                Colors = points?.Select(p => p.Label.ToLowerInvariant() switch
                {
                    "verified" => "success",
                    "pending" => "warning",
                    "flagged" => "danger",
                    _ => "muted",
                }).ToList(),
            },
        },
    };

    public static ChartSpec Mileage(IReadOnlyList<ChartPoint>? points) => new()
    {
        Kind = "bar",
        Suffix = " km",
        Labels = points?.Select(p => p.Label).ToList() ?? new(),
        Datasets = { new ChartSeries { Label = "Distance", Color = "accent", Data = Values(points) } },
    };

    private static List<double> Values(IReadOnlyList<ChartPoint>? points) =>
        points?.Select(p => (double)p.Value).ToList() ?? new();

    /// <summary>"BreakdownRepair" -> "Breakdown Repair".</summary>
    private static string Humanize(string label) => Regex.Replace(label, "(?<=[a-z])(?=[A-Z])", " ");
}
