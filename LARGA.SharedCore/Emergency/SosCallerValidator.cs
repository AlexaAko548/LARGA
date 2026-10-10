using System;
using LARGA.Shared.Models.Entities;

namespace LARGA.SharedCore.Emergency;

/// <summary>
/// SOS caller validation: an alert only counts as genuine when it comes from the driver who is
/// actively assigned to that taxi unit for the current shift, and the phone number it carries
/// strictly matches that driver's registered number. Pure rules - SosCallerVerificationService
/// loads the shift and the driver's profile from Firestore.
/// </summary>
public static class SosCallerValidator
{
    public readonly record struct Result(bool Verified, string Reason);

    public static Result Validate(EmergencyAlert alert, ShiftLog? shift, UserProfile? driver)
    {
        if (string.IsNullOrWhiteSpace(alert.ShiftId) || shift is null)
        {
            return Fail("No shift found for this alert.");
        }

        if (!string.Equals(shift.Status, "Active", StringComparison.OrdinalIgnoreCase) || shift.ShiftEnd is not null)
        {
            return Fail("The shift on this alert is no longer active.");
        }

        if (string.IsNullOrWhiteSpace(alert.DriverId) || !string.Equals(shift.DriverId, alert.DriverId, StringComparison.Ordinal))
        {
            return Fail("The sender is not the driver assigned to this shift.");
        }

        if (!string.IsNullOrWhiteSpace(alert.TaxiId)
            && !string.Equals(alert.TaxiId.Trim(), shift.TaxiId?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return Fail("The taxi unit on this alert doesn't match the shift's unit.");
        }

        if (driver is null)
        {
            return Fail("The driver's profile could not be found.");
        }

        string? registered = InputValidator.NormalizePhilippineMobile(driver.PhoneNumber);
        if (registered is null)
        {
            return Fail("The assigned driver has no valid registered phone number.");
        }

        // Strict: both sides must normalize to the same +639XXXXXXXXX - no lenient text compare.
        string? caller = InputValidator.NormalizePhilippineMobile(alert.CallerPhone);
        if (caller is null)
        {
            return Fail("The alert carries no caller phone number.");
        }
        if (caller != registered)
        {
            return Fail("The caller's phone number doesn't match the assigned driver's registered number.");
        }

        // The SIM's own number, when Android exposed it, must be the registered one too.
        if (!string.IsNullOrWhiteSpace(alert.DevicePhone)
            && InputValidator.NormalizePhilippineMobile(alert.DevicePhone) != registered)
        {
            return Fail("The phone that sent this alert isn't the assigned driver's registered number.");
        }

        return new Result(true, "Caller matches the driver assigned to this unit's active shift.");
    }

    private static Result Fail(string reason) => new(false, reason);
}
