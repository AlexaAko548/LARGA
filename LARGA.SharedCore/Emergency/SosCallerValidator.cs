using System;
using LARGA.Shared.Models.Entities;

namespace LARGA.SharedCore.Emergency;

/// <summary>Outcome of SOS caller validation, stored as emergency_alerts.callerCheck.</summary>
public static class SosCallerCheck
{
    /// <summary>Sent by the driver actively assigned to the unit's shift, from their registered number.</summary>
    public const string Verified = "Verified";

    /// <summary>Couldn't be fully confirmed - details missing (weak signal, an older app build) or a soft
    /// mismatch such as the SIM number or unit. Still pushed, with an "unverified caller" note: a real SOS
    /// must never be held back for lack of data.</summary>
    public const string Unverified = "Unverified";

    /// <summary>Positively from the wrong sender: the shift belongs to another driver, or the caller's number
    /// isn't the assigned driver's registered number. Not pushed; still listed (flagged) for managers.</summary>
    public const string Rejected = "Rejected";
}

/// <summary>
/// SOS caller validation: an alert only counts as genuine when it comes from the driver who is
/// actively assigned to that taxi unit for the current shift, and the phone number it carries
/// strictly matches that driver's registered number. Only a positive mismatch rejects an alert;
/// missing or unconfirmable details leave it Unverified (still pushed, flagged), because a false
/// rejection would silence a real emergency. Pure rules - SosCallerVerificationService loads the
/// shift and the driver's profile from Firestore.
/// </summary>
public static class SosCallerValidator
{
    public readonly record struct Result(string Status, string Reason)
    {
        public bool Verified => Status == SosCallerCheck.Verified;
        public bool Rejected => Status == SosCallerCheck.Rejected;
    }

    public static Result Validate(EmergencyAlert alert, ShiftLog? shift, UserProfile? driver)
    {
        if (string.IsNullOrWhiteSpace(alert.ShiftId) || shift is null)
        {
            return Unverified("No shift found for this alert.");
        }

        // Positive mismatch: the shift is someone else's. (firestore.rules already refuse this
        // write from the driver app, so it means the alert came from somewhere it shouldn't.)
        if (string.IsNullOrWhiteSpace(alert.DriverId) || !string.Equals(shift.DriverId, alert.DriverId, StringComparison.Ordinal))
        {
            return Rejected("The sender is not the driver assigned to this shift.");
        }

        string? registered = InputValidator.NormalizePhilippineMobile(driver?.PhoneNumber);
        string? caller = InputValidator.NormalizePhilippineMobile(alert.CallerPhone);

        // Strict: both sides normalize to the same +639XXXXXXXXX - no lenient text compare.
        if (registered is not null && caller is not null && caller != registered)
        {
            return Rejected("The caller's phone number doesn't match the assigned driver's registered number.");
        }

        // Soft checks - flagged, never rejected.
        if (!string.Equals(shift.Status, "Active", StringComparison.OrdinalIgnoreCase) || shift.ShiftEnd is not null)
        {
            return Unverified("The shift on this alert had already ended (the SOS may have been queued offline).");
        }
        if (driver is null)
        {
            return Unverified("The driver's profile could not be found.");
        }
        if (registered is null)
        {
            return Unverified("The assigned driver has no valid registered phone number.");
        }
        if (caller is null)
        {
            return Unverified("The alert carries no caller phone number (weak signal or an older app version).");
        }
        if (!string.IsNullOrWhiteSpace(alert.TaxiId)
            && !string.Equals(alert.TaxiId.Trim(), shift.TaxiId?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return Unverified("The taxi unit on this alert doesn't match the shift's unit.");
        }
        // The SIM's own number is unreliable (dual SIM, carriers writing stale numbers), so a
        // mismatch there only flags the alert.
        if (!string.IsNullOrWhiteSpace(alert.DevicePhone)
            && InputValidator.NormalizePhilippineMobile(alert.DevicePhone) != registered)
        {
            return Unverified("The phone's SIM number isn't the assigned driver's registered number.");
        }

        return new Result(SosCallerCheck.Verified, "Caller matches the driver assigned to this unit's active shift.");
    }

    private static Result Unverified(string reason) => new(SosCallerCheck.Unverified, reason);

    private static Result Rejected(string reason) => new(SosCallerCheck.Rejected, reason);
}
