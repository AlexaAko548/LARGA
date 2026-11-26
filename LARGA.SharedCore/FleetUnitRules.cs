using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace LARGA.SharedCore;

/// <summary>
/// Rules for adding, editing and deleting taxi units (ManagerWeb's Fleet Units page). Each Validate*
/// method returns null when the value is fine, otherwise the message to show - the same convention as
/// <see cref="InputValidator"/>.
/// </summary>
public static class FleetUnitRules
{
    public const int MinYear = 1980;
    public const int MaxMileage = 2_000_000;
    public const int MaxModelLength = 60;

    private const string IdPrefix = "TAXI_";

    /// <summary>Upper-case with single spaces, the form it's stored in.</summary>
    public static string NormalizePlate(string? plate) => InputValidator.NormalizeSpaces(plate).ToUpperInvariant();

    /// <summary>Letters and digits only, so "GAB 4004", "gab-4004" and "GAB4004" count as one plate.</summary>
    public static string PlateKey(string? plate) =>
        new(NormalizePlate(plate).Where(char.IsLetterOrDigit).ToArray());

    public static string? ValidatePlate(string? plate)
    {
        string value = NormalizePlate(plate);
        if (value.Length == 0)
        {
            return "Enter the plate number.";
        }

        return Regex.IsMatch(value, "^[A-Z0-9][A-Z0-9 -]{3,14}$") && value.Any(char.IsLetter) && value.Any(char.IsDigit)
            ? null
            : "Enter a plate with letters and numbers (4-15 characters), e.g. GAB 1234.";
    }

    public static string? ValidateModel(string? model) =>
        InputValidator.ValidateText(model, "the model", MaxModelLength, required: true, minLength: 2);

    /// <summary><paramref name="today"/>'s year plus one is allowed: next year's models are sold early.</summary>
    public static string? ValidateYear(int? year, DateTime today) =>
        year is not int value ? "Enter the year the unit was made."
        : value < MinYear || value > today.Year + 1 ? $"Year must be between {MinYear} and {today.Year + 1}."
        : null;

    public static string? ValidateMileage(int? mileage) =>
        InputValidator.ValidateWholeNumber(mileage, "the mileage", 0, MaxMileage);

    /// <summary>The next free ID after the highest TAXI_nnn in use.</summary>
    public static string NextTaxiId(IEnumerable<string> existingIds)
    {
        int highest = existingIds
            .Select(id => id.StartsWith(IdPrefix, StringComparison.OrdinalIgnoreCase)
                          && int.TryParse(id[IdPrefix.Length..], out int n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max();
        return $"{IdPrefix}{highest + 1:D3}";
    }

    /// <summary>Why a unit can't be deleted or retired right now; empty when it can.</summary>
    public static List<string> RemovalBlockers(UnitRemovalFacts facts)
    {
        var blockers = new List<string>();
        if (facts.AssignedDrivers.Count > 0)
        {
            blockers.Add($"It's assigned to {string.Join(", ", facts.AssignedDrivers)}. Give them another unit (or none) in their profile first.");
        }
        if (facts.HasActiveShift)
        {
            blockers.Add("It's out on a shift. Wait until the driver clocks out.");
        }
        if (facts.OpenGarageJobs > 0)
        {
            blockers.Add($"It has {facts.OpenGarageJobs} open Garage report(s) or work order(s). Resolve or dismiss them in the Garage first.");
        }
        if (facts.HasPendingClockIn)
        {
            blockers.Add("A clock-in for it is waiting for approval. Approve or deny it on Driver & Shifts first.");
        }
        if (facts.UpcomingSubstituteDates.Count > 0)
        {
            string dates = string.Join(", ", facts.UpcomingSubstituteDates.OrderBy(d => d).Take(5).Select(d => d.ToString("MMM d")));
            blockers.Add($"It's booked as a substitute on {dates}. Remove those bookings in the Schedule Planner first.");
        }
        return blockers;
    }
}

/// <summary>What's currently tied to a unit, as <see cref="FleetUnitRules.RemovalBlockers"/> needs it.</summary>
public record UnitRemovalFacts(
    IReadOnlyList<string> AssignedDrivers,
    bool HasActiveShift,
    int OpenGarageJobs,
    bool HasPendingClockIn,
    IReadOnlyList<DateTime> UpcomingSubstituteDates);
