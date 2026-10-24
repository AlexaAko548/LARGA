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
    // "SHELL SELECT", "SHE11" and OCR-joined words like "PETRONCORP" all count. "Shellane" (an LPG
    // brand sold at other stations too) doesn't.
    private static readonly (string Brand, Regex Pattern)[] NamePatterns =
    {
        (Petron, new Regex(@"(?<![A-Z0-9])P[E3]TR[O0Q]N", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        (Shell, new Regex(@"(?<![A-Z0-9])SH[E3][L1I|]{2}(?!ANE)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
    };

    // Dealer-run stations often print the operator's company name in the header instead of the
    // brand (e.g. "MH FUELS & MORE INC." for a Shell station) - their fuel products still name it:
    // Petron XCS / Blaze / Xtra Advance / Turbo Diesel / Diesel Max, Shell V-Power ("SVP") / FuelSave.
    private static readonly (string Brand, Regex Pattern)[] ProductPatterns =
    {
        (Petron, new Regex(@"(?<![A-Z0-9])XCS(?![A-Z])|BLAZE\s*100|XTRA\s*ADVANCE|TURBO\s*DIESEL|DIESEL\s*MAX", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        (Shell, new Regex(@"V\s*-?\s*POWER|(?<![A-Z0-9])SVP\s*-?\s*(?:GAS|DIESEL|RACING|\b)|FUEL\s*SAVE", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
    };

    /// <summary>"Petron" or "Shell" when the text (an OCR'd receipt, or the station name the driver
    /// submitted) names one of them - by name first, then by its fuel products; otherwise null.</summary>
    public static string? Match(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return NamePatterns.FirstOrDefault(p => p.Pattern.IsMatch(text)).Brand
               ?? ProductPatterns.FirstOrDefault(p => p.Pattern.IsMatch(text)).Brand;
    }

    public static bool IsAllowed(string? stationName) => Match(stationName) is not null;
}
