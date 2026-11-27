using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LARGA.SharedCore.Ledger;

/// <summary>A boundary rate that was replaced: it still applies to shifts that started before
/// <paramref name="UntilUtc"/>.</summary>
public sealed record PastBoundaryRate(DateTime UntilUtc, decimal Rate);

/// <summary>
/// The daily boundary over time (system_configs/global): defaultBoundaryRate is today's rate and
/// boundaryRateHistory keeps the ones it replaced, so changing the rate never re-prices a shift
/// that had already started. Web and phones both resolve a shift's rate here.
/// A payment record's own expectedBoundary still wins over this.
/// </summary>
public sealed record BoundaryRateSchedule(decimal CurrentRate, IReadOnlyList<PastBoundaryRate>? PastRates = null)
{
    private const char Separator = '|';

    /// <summary>The boundary for a shift that started at <paramref name="shiftStartUtc"/>: the
    /// earliest replaced rate that was still in effect then, otherwise the current one.</summary>
    public decimal RateFor(DateTime? shiftStartUtc)
    {
        if (shiftStartUtc is not DateTime start || PastRates is null) return CurrentRate;

        PastBoundaryRate? inEffect = PastRates
            .Where(p => start < p.UntilUtc)
            .OrderBy(p => p.UntilUtc)
            .FirstOrDefault();
        return inEffect?.Rate ?? CurrentRate;
    }

    /// <summary>One boundaryRateHistory entry, e.g. "2026-11-27T03:15:00.0000000Z|800". Plain text
    /// so the Admin SDK and the phones' Firestore plugin read it the same way.</summary>
    public static string HistoryEntry(DateTime untilUtc, decimal rate) =>
        string.Create(CultureInfo.InvariantCulture, $"{DateTime.SpecifyKind(untilUtc, DateTimeKind.Utc):O}{Separator}{rate}");

    /// <param name="configuredRate">defaultBoundaryRate; the shared fallback when not positive.</param>
    /// <param name="history">boundaryRateHistory; entries that can't be read are skipped.</param>
    public static BoundaryRateSchedule From(decimal configuredRate, IEnumerable<string?>? history)
    {
        var past = new List<PastBoundaryRate>();
        foreach (string? entry in history ?? Enumerable.Empty<string?>())
        {
            string[] parts = (entry ?? string.Empty).Split(Separator);
            if (parts.Length == 2
                && DateTime.TryParse(parts[0], CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime until)
                && decimal.TryParse(parts[1], NumberStyles.Number, CultureInfo.InvariantCulture, out decimal rate)
                && rate > 0)
            {
                past.Add(new PastBoundaryRate(until, rate));
            }
        }

        return new BoundaryRateSchedule(configuredRate > 0 ? configuredRate : ShiftRules.DefaultBoundaryRate, past);
    }

    /// <summary>A single rate for every shift (no history).</summary>
    public static implicit operator BoundaryRateSchedule(decimal rate) => new(rate);
}
