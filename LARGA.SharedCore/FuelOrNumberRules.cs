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

    /// <summary>The fuelLogDetails for a log flagged for more than one reason, e.g.
    /// "Station is not Petron or Shell; OR number already submitted". Null when there are none.</summary>
    public static string? CombineReasons(IEnumerable<string?> reasons)
    {
        string[] present = reasons.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r!.Trim()).Distinct().ToArray();
        return present.Length == 0 ? null : string.Join("; ", present);
    }
}
