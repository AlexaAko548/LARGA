using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace LARGA.SharedCore;

/// <summary>
/// FUEL-004 / UAT-038: BLM fuels only at Petron and Shell. The one list every part of the app
/// checks against - the driver app's receipt scan and submit (a receipt from anywhere else is
/// saved Flagged with <see cref="NotAllowedReason"/>), the manager's fuel queue and the seed data.
/// </summary>
public static class FuelStations
{
    public const string Petron = "Petron";
    public const string Shell = "Shell";

    public static readonly IReadOnlyList<string> Allowed = new[] { Petron, Shell };

    /// <summary>fuel_logs.fuelLogDetails on a receipt from any other station.</summary>
    public const string NotAllowedReason = "Station is not Petron or Shell";

    // Matched as words anywhere in the text, case-insensitive, allowing the usual OCR slips
    // (0 for O, 3 for E, 1/I/| for L): "PETRON CORPORATION", "Petron - EDSA", "Shell Pilipinas",
    // "SHELL SELECT", "SHE11" and OCR-joined words like "PETRONCORP" all count.
    private static readonly (string Brand, Regex Pattern)[] Patterns =
    {
        (Petron, new Regex(@"(?<![A-Z0-9])P[E3]TR[O0Q]N", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        (Shell, new Regex(@"(?<![A-Z0-9])SH[E3][L1I|]{2}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
    };

    /// <summary>"Petron" or "Shell" when the text (an OCR'd receipt, or the station name the driver
    /// submitted) names one of them; otherwise null.</summary>
    public static string? Match(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return Patterns.FirstOrDefault(p => p.Pattern.IsMatch(text)).Brand;
    }

    public static bool IsAllowed(string? stationName) => Match(stationName) is not null;
}
