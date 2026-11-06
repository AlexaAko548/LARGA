using System;
using System.Collections.Generic;
using System.Linq;

namespace LARGA.SharedCore;

/// <summary>
/// Proposal §4.2.4.5: a fuel receipt's OR number can be submitted only once. Same idea as
/// PaymentReferenceRules (BRP-008): the driver app claims fuel_or_numbers/{normalized OR number}
/// in the same transaction that saves the fuel log. Unlike a duplicate GCash reference, a
/// duplicate OR number doesn't stop the save - the log goes in Flagged (autoFlagged) with
/// <see cref="DuplicateReason"/>, the same path as a non-Petron/Shell receipt, so the manager sees it
/// in the flagged queue and Alert Center. A blank OR number is never checked.
/// </summary>
public static class FuelOrNumberRules
{
    public const string Collection = "fuel_or_numbers";

    /// <summary>fuel_logs.fuelLogDetails on a receipt whose OR number was already submitted.</summary>
    public const string DuplicateReason = "OR number already submitted";

    /// <summary>The claim document ID: trimmed, spaces and dashes removed, upper-case
    /// (PaymentReferenceRules.Normalize). Null when the OR number is blank.</summary>
    public static string? Normalize(string? orNumber) => PaymentReferenceRules.Normalize(orNumber);

    /// <summary>
    /// The fuel_or_numbers claim key: station + OR number, not the OR number alone. An OR number is
    /// only unique per issuing station/branch - two different stations (or branches) can legitimately
    /// print the same number, and keying by OR number alone wrongly flagged that as a duplicate
    /// (consistency audit row #58). Station is normalized the same way as the OR number (trimmed,
    /// spaces/dashes removed, upper-case), which also absorbs minor OCR formatting differences
    /// between reads of the same station name. A Petron or Shell station is keyed by its brand
    /// (FuelStations.Match), so editing the station text ("Petron" to "Petron EDSA") can't dodge the
    /// check; the cost is that two branches of the same brand printing the same number are flagged
    /// for the manager to review. Null when the OR number is blank; a blank/unreadable
    /// station still gets a key (grouped under "UNKNOWN") so the OR number is still checked.
    /// </summary>
    public static string? Key(string? station, string? orNumber)
    {
        string? orKey = Normalize(orNumber);
        if (orKey is null)
        {
            return null;
        }

        // Brand, not the raw text: "Petron" and "Petron - EDSA" must be the same claim, or editing the
        // station on the receipt would dodge the duplicate check. Other stations keep their own text.
        string stationKey = PaymentReferenceRules.Normalize(FuelStations.Match(station) ?? station) ?? "UNKNOWN";
        return $"{stationKey}~{orKey}";
    }

    /// <summary>True for a claim document ID, false for a backfill marker ("__backfill_...") - what the
    /// backfill clears before re-claiming under the current keys.</summary>
    public static bool IsClaimId(string? documentId) =>
        !string.IsNullOrEmpty(documentId) && !documentId.StartsWith("__backfill_", StringComparison.Ordinal);

    /// <summary>The fuelLogDetails for a log flagged for more than one reason, e.g.
    /// "Station is not Petron or Shell; OR number already submitted". Null when there are none.</summary>
    public static string? CombineReasons(IEnumerable<string?> reasons)
    {
        string[] present = reasons.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r!.Trim()).Distinct().ToArray();
        return present.Length == 0 ? null : string.Join("; ", present);
    }
}
