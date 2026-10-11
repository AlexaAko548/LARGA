using LARGA.Shared.Models.Entities;
using Microsoft.Maui.Storage;
using Plugin.Firebase.Auth;
using Plugin.Firebase.Firestore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace LARGA.SharedCore.Services;

public interface IShiftManagementService
{
    Task<bool> CreateShiftScheduleAsync(ShiftSchedule schedule);
    Task<string> StartShiftLogAsync(ShiftLog shift);
    Task<bool> UpdateTaxiStatusAsync(string taxiId, string newStatus);
    Task<TaxiUnit> GetTaxiUnitAsync(string taxiId);
    Task<TaxiUnit> GetCurrentUserAssignedTaxiAsync();

    /// <summary>The unit the signed-in driver drives today: a substitute the manager assigned
    /// for today (while their own unit is under maintenance), else their permanent unit.</summary>
    Task<string?> GetTodaysTaxiIdAsync(string? permanentTaxiId = null);
    Task<string> ClockInAsync(string taxiId, int startMileage);

    /// <summary>Why the signed-in driver can't start a shift on this unit today - no valid
    /// license (LAR-97) or the unit is under maintenance (LAR-98), per ShiftEligibilityRules -
    /// as a driver-facing sentence, or null when they can.</summary>
    Task<string?> GetClockInBlockReasonAsync(string taxiId);
    Task<ShiftEndCharges> ClockOutAsync(string shiftDocumentId, int endMileage, bool fuelBelowHalf, string managerNote = "");
    Task<DriverShiftSummary?> GetMyOpenShiftAsync();
    Task SetOnBreakAsync(string shiftDocumentId, bool isOnBreak);
    Task<bool> SubmitHandoverChecklistAsync(HandoverChecklistSubmission checklist);

    /// <summary>Where units must be returned: system_configs/global's garage fields when set,
    /// else the ShiftRules defaults.</summary>
    Task<GarageGeofence> GetGarageGeofenceAsync();

    /// <summary>Sends a flagged pre-shift inspection to the manager (clockin_requests, status
    /// Pending) instead of clocking in. Returns the request's document ID.</summary>
    Task<string> SubmitClockInRequestAsync(ClockInRequestSubmission request);

    /// <summary>The request's current state, or null if it can't be read.</summary>
    Task<ClockInRequestStatus?> GetClockInRequestAsync(string requestId);

    /// <summary>Driver withdraws a still-pending request.</summary>
    Task CancelClockInRequestAsync(string requestId);

    /// <summary>After an approved request's shift has started: links it and marks the request used.</summary>
    Task MarkClockInRequestUsedAsync(string requestId, string shiftId);

    /// <summary>Debug builds only: applies system_configs/global "testClockPh" to ShiftClock
    /// so shift rules can be tested at any hour. No-op in release builds.</summary>
    Task RefreshTestClockAsync();
}

public record GarageGeofence(double Latitude, double Longitude, double RadiusMeters);

/// <summary>A flagged pre-shift inspection, sent for the manager's evaluation
/// (clockin_requests - see LARGA.Shared.Models.Entities.ClockInRequest).</summary>
public class ClockInRequestSubmission
{
    public string TaxiId { get; set; } = string.Empty;
    public int StartMileage { get; set; }
    public IReadOnlyDictionary<string, bool> Inspection { get; set; } = new Dictionary<string, bool>();
    public bool IsBelowHalfTank { get; set; }
    public string? FuelDashboardUrl { get; set; }
    public string? OdometerPhotoUrl { get; set; }
    public IReadOnlyList<string> DefectReportIds { get; set; } = Array.Empty<string>();
    public string FlagReasons { get; set; } = string.Empty;
}

/// <summary>What the waiting screen needs from a clock-in request.</summary>
public class ClockInRequestStatus
{
    public string Status { get; set; } = string.Empty;
    public string TaxiId { get; set; } = string.Empty;
    public int StartMileage { get; set; }
    public string FlagReasons { get; set; } = string.Empty;
    public string? ManagerNote { get; set; }
    public string? ShiftId { get; set; }
    public HandoverChecklistSubmission Checklist { get; set; } = new();
}

/// <summary>What clock-out added on top of the boundary (ShiftRules).</summary>
public record ShiftEndCharges(decimal LateFee, decimal FuelPenalty)
{
    public decimal Total => LateFee + FuelPenalty;
}

public class DriverShiftSummary
{
    public string DocumentId { get; set; } = string.Empty;
    public string TaxiId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime ShiftStartUtc { get; set; }
    public int EndMileage { get; set; }
}

/// <summary>
/// One pre-/end-shift inspection as the driver completed it on the phone, written to
/// handover_checklists so ManagerWeb's Shift Logs can show it. Plain values here; the
/// Firestore string encodings (e.g. "Pre-Shift", "Half-tank") are applied on write to match
/// HandoverChecklist's converters.
/// </summary>
public class HandoverChecklistSubmission
{
    public string ShiftId { get; set; } = string.Empty;
    public bool IsEndShift { get; set; }
    public bool TireCondition { get; set; }
    public bool UnderTheHood { get; set; }
    public bool LightsCondition { get; set; }
    public bool InteriorCleanliness { get; set; }
    public bool ExteriorCondition { get; set; }
    public bool IsBelowHalfTank { get; set; }
    public string? FuelDashboardUrl { get; set; }
    public string? OdometerPhotoUrl { get; set; }
}

public class ShiftManagementService : IShiftManagementService
{
    public async Task<bool> CreateShiftScheduleAsync(ShiftSchedule schedule)
    {
        try
        {
            await CrossFirebaseFirestore.Current
                .GetCollection("shift_schedules")
                .AddDocumentAsync(schedule);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Schedule Error: {ex.Message}");
            return false;
        }
    }

    public async Task<string> StartShiftLogAsync(ShiftLog shift)
    {
        try
        {
            var docRef = await CrossFirebaseFirestore.Current
                .GetCollection("shifts")
                .AddDocumentAsync(shift);
            return docRef.Id;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Shift Error: {ex.Message}");
            return string.Empty;
        }
    }

    public async Task<bool> UpdateTaxiStatusAsync(string taxiId, string newStatus)
    {
        try
        {
            await CrossFirebaseFirestore.Current
                .GetCollection("taxis")
                .GetDocument(taxiId)
                .UpdateDataAsync(new Dictionary<object, object> { { "status", newStatus } });
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Taxi Status Error: {ex.Message}");
            return false;
        }
    }

    public async Task<TaxiUnit> GetTaxiUnitAsync(string taxiId)
    {
        try
        {
            var document = await CrossFirebaseFirestore.Current
                .GetCollection("taxis")
                .GetDocument(taxiId)
                .GetDocumentSnapshotAsync<TaxiUnitProxy>();

            if (document != null && document.Data != null)
            {
                var data = document.Data;
                return new TaxiUnit
                {
                    DocumentId = document.Reference.Id,
                    TaxiId = data.TaxiId ?? string.Empty,
                    Model = data.Model ?? string.Empty,
                    PlateNumber = data.PlateNumber ?? string.Empty,
                    Status = data.Status ?? string.Empty,
                    YearManufactured = data.YearManufactured
                };
            }
            return null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Fetch Taxi Error: {ex.Message}");
            return null;
        }
    }

    public async Task<TaxiUnit> GetCurrentUserAssignedTaxiAsync()
    {
        var user = CrossFirebaseAuth.Current.CurrentUser;
        if (user == null) return null;

        var profile = await CrossFirebaseFirestore.Current
            .GetCollection("users")
            .GetDocument(user.Uid)
            .GetDocumentSnapshotAsync<UserProfileProxy>();

        // Pre-shift screens and clock-in all come through here, so a substitute for today
        // is what the shift gets logged against.
        string? taxiId = await GetTodaysTaxiIdAsync(profile?.Data?.AssignedTaxiId);
        if (string.IsNullOrWhiteSpace(taxiId)) return null;

        return await GetTaxiUnitAsync(taxiId);
    }

    public async Task<string?> GetTodaysTaxiIdAsync(string? permanentTaxiId = null)
    {
        var user = CrossFirebaseAuth.Current.CurrentUser;
        if (user == null) return permanentTaxiId;

        try
        {
            // Same "{driverId}_{yyyyMMdd}" document ManagerWeb's Schedule Planner writes, keyed
            // by the Philippine calendar day.
            string docId = $"{user.Uid}_{PhilippineTime.Now:yyyyMMdd}";
            var schedule = await CrossFirebaseFirestore.Current
                .GetCollection("shift_schedules")
                .GetDocument(docId)
                .GetDocumentSnapshotAsync<ShiftScheduleProxy>();

            if (schedule?.Data?.Status == "Substitute" && !string.IsNullOrWhiteSpace(schedule.Data.TaxiId))
            {
                return schedule.Data.TaxiId;
            }
        }
        catch (Exception ex)
        {
            // No schedule entry (or no read access) just means no substitute today.
            System.Diagnostics.Debug.WriteLine($"Today's Schedule Error: {ex.Message}");
        }

        return permanentTaxiId;
    }

    /// <summary>
    /// Starts a shift. Enforces the operating-day rules (ShiftRules) before writing anything:
    /// no clock-in before 6:00 AM, and one open shift per driver (paper REQ-5.5-1). Rule
    /// violations throw <see cref="InvalidOperationException"/> with a driver-facing message.
    /// A leftover open shift past its auto-close time (6:00 AM the day after it started) is
    /// closed here as a missed clock-out - ManagerWeb's background check does the same, this
    /// just covers the minutes before it runs - and today's starting odometer becomes its end
    /// odometer, since the unit sat parked in between.
    /// </summary>
    public async Task<string> ClockInAsync(string taxiId, int startMileage)
    {
        var user = CrossFirebaseAuth.Current.CurrentUser;
        if (user == null) throw new Exception("No authenticated driver found.");

        await RefreshTestClockAsync();
        DateTime now = ShiftClock.UtcNow;
        if (!ShiftRules.CanClockIn(now))
        {
            throw new InvalidOperationException("Shifts start at 6:00 AM. You can clock in from 6:00 AM onwards.");
        }

        if (await GetClockInBlockReasonAsync(taxiId) is string blocked)
        {
            throw new InvalidOperationException(blocked);
        }

        foreach (DriverShiftSummary previous in await GetMyShiftsAsync(user.Uid))
        {
            bool stillOpen = previous.Status == "Active";
            bool pastAutoClose = now >= ShiftRules.AutoCloseAtUtc(previous.ShiftStartUtc);

            if (stillOpen && !pastAutoClose)
            {
                throw new InvalidOperationException(
                    $"You still have an open shift from {previous.ShiftStartUtc.ToPhilippineTime():MMM d, h:mm tt}. End that shift first.");
            }

            if ((stillOpen && pastAutoClose) || (previous.Status == AutoClosedStatus && previous.EndMileage <= 0))
            {
                var closeUpdate = new Dictionary<object, object>
                {
                    { "status", AutoClosedStatus },
                    { "isOnBreak", false },
                    { "endMileage", startMileage },
                };
                await CrossFirebaseFirestore.Current
                    .GetCollection("shifts")
                    .GetDocument(previous.DocumentId)
                    .UpdateDataAsync(closeUpdate);
            }
        }

        IFirebaseFirestore firestore = CrossFirebaseFirestore.Current;
        IDocumentReference shiftDoc = firestore.GetCollection("shifts").CreateDocument();
        IDocumentReference driverLock = firestore.GetCollection("shift_locks").GetDocument(DriverLockId(user.Uid));
        IDocumentReference unitLock = firestore.GetCollection("shift_locks").GetDocument(UnitLockId(taxiId));

        // shiftStart is stamped by the Firestore server, not the phone (firestore.rules refuse
        // anything else), so a phone clock set back can't start a shift before 6:00 AM or move
        // the late-fee deadline. A dictionary, because FieldValue doesn't fit a typed proxy.
        // shiftId = the document ID from the start: everything else that points at a shift
        // (fuel_logs, handover_checklists, boundary_payments, ManagerWeb) uses the two
        // interchangeably. One value for the shift and both locks: firestore.rules check that a
        // lock carries the same shiftStart as the shift it claims.
        object shiftStart = ShiftTimestamp();
        var shiftFields = new Dictionary<object, object>
        {
            { "driverId", user.Uid },
            { "taxiId", taxiId },
            { "shiftStart", shiftStart },
            { "startMileage", startMileage },
            { "status", "Active" },
            { "shiftId", shiftDoc.Id },
            { "isOnBreak", false },
            { "managerNote", string.Empty },
        };

        // One transaction checks and claims both the driver and the unit, so a double tap, two
        // phones, or two drivers on the same unit at the same moment can't both start a shift:
        // shift_locks/driver_{uid} and shift_locks/unit_{taxiId} point at the shift holding each
        // (see LockHolder for when a lock still counts).
        // The body returns a reason instead of throwing - see QuickLedgerService.SavePaymentPlanAsync.
        string? refusal = await firestore.RunTransactionAsync(transaction =>
        {
            string? driverBusy = LockHolder(transaction, firestore, driverLock, user.Uid, now);
            if (driverBusy is not null)
            {
                return "You still have an open shift. End that shift first.";
            }

            string? unitHolder = LockHolder(transaction, firestore, unitLock, user.Uid, now);
            if (unitHolder is not null && unitHolder != user.Uid)
            {
                return $"{taxiId} is already out on another driver's shift. Ask your manager for a substitute unit.";
            }

            transaction.SetData(shiftDoc, shiftFields, SetOptions.Merge());
            var lockFields = new Dictionary<object, object>
            {
                { "shiftId", shiftDoc.Id },
                { "driverId", user.Uid },
                { "shiftStart", shiftStart },
                { "released", false },
                { "updatedAt", FieldValue.ServerTimestamp() },
            };
            transaction.SetData(driverLock, lockFields, SetOptions.Merge());
            transaction.SetData(unitLock, lockFields, SetOptions.Merge());
            return (string?)null;
        });

        if (refusal is not null)
        {
            throw new InvalidOperationException(refusal);
        }

        return shiftDoc.Id;
    }

    private static string DriverLockId(string driverId) => $"driver_{driverId}";

    private static string UnitLockId(string taxiId) => $"unit_{taxiId}";

    /// <summary>
    /// The driver holding <paramref name="lockDoc"/>, or null when it's free: no lock yet, released
    /// at clock-out (ClockOutAsync), or its shift is past its 6:00 AM auto-close (ShiftRules) - a
    /// missed clock-out from an earlier day, not a live shift.
    /// Decided from the lock itself when it's another driver's: firestore.rules don't let a driver
    /// read someone else's shift, and inside a transaction that refusal fails the whole clock-in.
    /// A lock of the caller's own is double-checked against their shift (which they can read).
    /// </summary>
    private static string? LockHolder(ITransaction transaction, IFirebaseFirestore firestore, IDocumentReference lockDoc,
        string callerId, DateTime nowUtc)
    {
        ShiftLockProxy? shiftLock = transaction.GetDocument<ShiftLockProxy>(lockDoc)?.Data;
        if (shiftLock is null || string.IsNullOrWhiteSpace(shiftLock.ShiftId) || string.IsNullOrWhiteSpace(shiftLock.DriverId)
            || shiftLock.Released)
        {
            return null;
        }

        DateTime? start = ReadPluginDate(shiftLock.ShiftStart);
        if (shiftLock.DriverId == callerId)
        {
            ShiftLockedShiftProxy? shift = transaction
                .GetDocument<ShiftLockedShiftProxy>(firestore.GetCollection("shifts").GetDocument(shiftLock.ShiftId))?.Data;
            if (shift is null || !string.Equals(shift.Status, "Active", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            start = ReadPluginDate(shift.ShiftStart);
        }
        else if (start is null)
        {
            // Written before locks carried shiftStart (and never released): nothing to tell a live
            // shift from an old one, so it doesn't block - the same as before shift_locks existed.
            return null;
        }

        return start is DateTime started && nowUtc >= ShiftRules.AutoCloseAtUtc(started) ? null : shiftLock.DriverId;
    }

    private class ShiftLockProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("shiftId")]
        public string? ShiftId { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("driverId")]
        public string? DriverId { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("shiftStart")]
        public DateTime ShiftStart { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("released")]
        public bool Released { get; set; }
    }

    private class ShiftLockedShiftProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("driverId")]
        public string? DriverId { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("status")]
        public string? Status { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("shiftStart")]
        public DateTime ShiftStart { get; set; }
    }

    public async Task<string?> GetClockInBlockReasonAsync(string taxiId)
    {
        var user = CrossFirebaseAuth.Current.CurrentUser;
        if (user == null) throw new Exception("No authenticated driver found.");

        await RefreshTestClockAsync();
        DateTime todayPh = ShiftClock.UtcNow.ToPhilippineTime().Date;

        (string? reason, bool isLicense) = await GetBlockReasonAsync(user.Uid, taxiId, todayPh);
        return reason is null ? null
            : isLicense ? $"You can't start a shift: {reason}. Please have your manager update your license."
            : $"You can't start a shift: {reason}. Please ask your manager for a substitute unit.";
    }

    /// <summary>
    /// ShiftEligibilityRules for any driver and unit, read with the client SDK - the driver's own
    /// clock-in and the manager app's clock-in approval both use it (ManagerWeb's
    /// ClockInApprovalService does the same with the Admin SDK). Returns a short phrase such as
    /// "no driver's license is on file", and whether it's the license (vs the unit).
    /// </summary>
    public static async Task<(string? Reason, bool IsLicense)> GetBlockReasonAsync(string driverId, string taxiId, DateTime todayPh)
    {
        var profile = await CrossFirebaseFirestore.Current
            .GetCollection("users")
            .GetDocument(driverId)
            .GetDocumentSnapshotAsync<EligibilityUserProxy>();
        DateTime? licenseExpiry = profile?.Data?.LicenseExpiryDate?.UtcDateTime;
        if (ShiftEligibilityRules.LicenseBlockReason(licenseExpiry, todayPh) is string licenseReason)
        {
            return (licenseReason, true);
        }

        if (string.IsNullOrWhiteSpace(taxiId))
        {
            return (null, false);
        }

        var taxi = await CrossFirebaseFirestore.Current
            .GetCollection("taxis")
            .GetDocument(taxiId)
            .GetDocumentSnapshotAsync<TaxiUnitProxy>();
        // Single equality filter on taxiId (no composite index), the rest client-side.
        var jobs = await CrossFirebaseFirestore.Current
            .GetCollection("maintenance_logs")
            .WhereEqualsTo("taxiId", taxiId)
            .GetDocumentsAsync<EligibilityJobProxy>();
        var unitJobs = jobs.Documents
            .Where(d => d.Data != null)
            .Select(d => ((string?)d.Data.Status, WorkOrderRules.ShopStartUtc(d.Data.DateLogged.UtcDateTime, d.Data.ScheduledDate?.UtcDateTime), d.Data.EstimatedCompletionDate?.UtcDateTime, (string?)d.Data.IssueTitle));

        return (ShiftEligibilityRules.UnitBlockReason(taxiId, taxi?.Data?.Status, unitJobs, todayPh), false);
    }

    public async Task SetOnBreakAsync(string shiftDocumentId, bool isOnBreak)
    {
        try
        {
            await CrossFirebaseFirestore.Current
                .GetCollection("shifts")
                .GetDocument(shiftDocumentId)
                .UpdateDataAsync(new Dictionary<object, object> { { "isOnBreak", isOnBreak } });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Break Status Error: {ex.Message}");
        }
    }

    public async Task<bool> SubmitHandoverChecklistAsync(HandoverChecklistSubmission checklist)
    {
        try
        {
            var proxy = new HandoverChecklistProxy
            {
                ShiftId = checklist.ShiftId,
                ChecklistType = checklist.IsEndShift ? "End-Shift" : "Pre-Shift",
                TireCondition = checklist.TireCondition,
                OilLevel = checklist.UnderTheHood,
                CoolantLevel = checklist.UnderTheHood,
                LightsCondition = checklist.LightsCondition,
                InteriorCleanliness = checklist.InteriorCleanliness,
                ExteriorScratches = checklist.ExteriorCondition,
                FuelVerification = checklist.IsBelowHalfTank ? "Below half-tank" : "Half-tank",
                FuelDashboardUrl = checklist.FuelDashboardUrl ?? string.Empty,
                OdometerPhotoUrl = checklist.OdometerPhotoUrl ?? string.Empty,
                Timestamp = DateTime.UtcNow,
            };

            // Deterministic ID (one Pre + one End per shift, same "{shiftId}_PRE" convention
            // as the seed data) so a retried submit overwrites instead of duplicating.
            string docId = $"{checklist.ShiftId}_{(checklist.IsEndShift ? "END" : "PRE")}";
            await CrossFirebaseFirestore.Current
                .GetCollection("handover_checklists")
                .GetDocument(docId)
                .SetDataAsync(proxy);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Checklist Submit Error: {ex.Message}");
            return false;
        }
    }

    /// <summary>Ends the shift and records its low-fuel penalty (below half-tank at return).
    /// shiftEnd is stamped by the Firestore server (firestore.rules refuse a phone-clock value),
    /// and the late-return fee is NOT stored - every ledger works it out from shiftStart/shiftEnd
    /// (ShiftRules.EffectiveLateFee), so the phone can't set its own fee. Returns both charges so
    /// the Shift Completed screen can show today's total.</summary>
    public async Task<ShiftEndCharges> ClockOutAsync(string activeShiftId, int endMileage, bool fuelBelowHalf, string managerNote = "")
    {
        try
        {
            await RefreshTestClockAsync();
            var shiftDoc = CrossFirebaseFirestore.Current.GetCollection("shifts").GetDocument(activeShiftId);

            decimal fuelPenalty = fuelBelowHalf ? ShiftRules.LowFuelPenalty : 0m;
            var updateData = new Dictionary<object, object>
            {
                { "shiftEnd", ShiftTimestamp() },
                { "endMileage", endMileage },
                { "status", "Completed" },
                { "managerNote", managerNote },
                { "isOnBreak", false },
                { "fuelPenalty", (double)fuelPenalty },
            };

            // The shift's driver and unit locks are released in the same commit, so the unit is
            // free for the next driver the moment this shift ends (see LockHolder).
            IWriteBatch batch = CrossFirebaseFirestore.Current.CreateBatch();
            batch.UpdateData(shiftDoc, updateData);
            foreach (IDocumentReference lockDoc in await LocksHeldByAsync(activeShiftId))
            {
                batch.SetData(lockDoc, new Dictionary<object, object>
                {
                    { "released", true },
                    { "updatedAt", FieldValue.ServerTimestamp() },
                }, SetOptions.Merge());
            }
            await batch.CommitAsync();

            // Only for the Shift Completed screen: the same fee the ledgers will compute, from
            // the server's own timestamps now that they're saved.
            decimal lateFee = 0m;
            try
            {
                var snapshot = await shiftDoc.GetDocumentSnapshotAsync<ShiftTimesProxy>();
                DateTime? start = snapshot?.Data is null ? null : ReadPluginDate(snapshot.Data.ShiftStart);
                DateTime? end = snapshot?.Data is null ? null : ReadPluginDate(snapshot.Data.ShiftEnd);
                lateFee = ShiftRules.EffectiveLateFee(null, start, end ?? ShiftClock.UtcNow) ?? 0m;
            }
            catch (Exception ex)
            {
                // Display only - the ledgers still charge the right fee from the saved times.
                System.Diagnostics.Debug.WriteLine($"Late Fee Preview Error: {ex.Message}");
            }

            return new ShiftEndCharges(lateFee, fuelPenalty);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Service Error: {ex.Message}");
            throw;
        }
    }

    /// <summary>The shift_locks documents that point at this shift - only those may be released
    /// (firestore.rules refuse touching a lock that holds another shift). On a failed read none
    /// are returned: the shift still ends, and its locks lapse at its 6:00 AM auto-close.</summary>
    private static async Task<List<IDocumentReference>> LocksHeldByAsync(string shiftId)
    {
        var held = new List<IDocumentReference>();
        try
        {
            IFirebaseFirestore firestore = CrossFirebaseFirestore.Current;
            var shift = await firestore.GetCollection("shifts").GetDocument(shiftId).GetDocumentSnapshotAsync<ShiftReadProxy>();
            string? driverId = CrossFirebaseAuth.Current.CurrentUser?.Uid;
            string? taxiId = shift?.Data?.TaxiId;

            var lockIds = new List<string>();
            if (!string.IsNullOrWhiteSpace(driverId)) lockIds.Add(DriverLockId(driverId));
            if (!string.IsNullOrWhiteSpace(taxiId)) lockIds.Add(UnitLockId(taxiId));

            foreach (string lockId in lockIds)
            {
                IDocumentReference lockDoc = firestore.GetCollection("shift_locks").GetDocument(lockId);
                var snapshot = await lockDoc.GetDocumentSnapshotAsync<ShiftLockProxy>();
                if (snapshot?.Data?.ShiftId == shiftId)
                {
                    held.Add(lockDoc);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Shift lock lookup failed: {ex.Message}");
        }
        return held;
    }

    public const string AutoClosedStatus = "Auto-Closed";

    /// <summary>The signed-in driver's shift that's still open (status Active), if any - used
    /// to put the phone back into its active-shift state when it has lost track (reinstall,
    /// cleared data, different phone), so the driver can end it normally.</summary>
    public async Task<DriverShiftSummary?> GetMyOpenShiftAsync()
    {
        var user = CrossFirebaseAuth.Current.CurrentUser;
        if (user == null) return null;

        await RefreshTestClockAsync();
        return (await GetMyShiftsAsync(user.Uid))
            .Where(s => s.Status == "Active")
            .OrderByDescending(s => s.ShiftStartUtc)
            .FirstOrDefault();
    }

    private static async Task<List<DriverShiftSummary>> GetMyShiftsAsync(string driverId)
    {
        // Single equality filter (auto-indexed); status is checked client-side.
        var query = await CrossFirebaseFirestore.Current
            .GetCollection("shifts")
            .WhereEqualsTo("driverId", driverId)
            .GetDocumentsAsync<ShiftReadProxy>();

        return query.Documents
            .Where(d => d.Data != null && (d.Data.Status == "Active" || d.Data.Status == AutoClosedStatus))
            .Select(d => new DriverShiftSummary
            {
                DocumentId = d.Reference.Id,
                TaxiId = d.Data.TaxiId ?? string.Empty,
                Status = d.Data.Status,
                ShiftStartUtc = FixPluginDate(d.Data.ShiftStart),
                EndMileage = d.Data.EndMileage,
            })
            .ToList();
    }

    // Same Plugin.Firebase (Android) date bug and correction as
    // LARGA.MobileApp.Services.FirestoreDateTimeFix: timestamps come back as FromFileTimeUtc of
    // the Unix millisecond value, i.e. around the year 1601.
    private static DateTime FixPluginDate(DateTime value)
    {
        if (value.Year > 1700) return DateTime.SpecifyKind(value, DateTimeKind.Utc);
        long millis = value.Ticks - new DateTime(1601, 1, 1).Ticks;
        return DateTimeOffset.FromUnixTimeMilliseconds(millis).UtcDateTime;
    }

    /// <summary>The value to write for shiftStart/shiftEnd: Firestore server time, which
    /// firestore.rules require. Debug builds running the test clock (system_configs/global
    /// testClockPh - the rules accept any time only while it's set) write the pretend time.</summary>
    private static object ShiftTimestamp() =>
        ShiftClock.IsPretending ? ShiftClock.UtcNow : FieldValue.ServerTimestamp();

    /// <summary>FixPluginDate for a field that may be missing (it then reads as default).</summary>
    private static DateTime? ReadPluginDate(DateTime value) =>
        value == default ? null : FixPluginDate(value);

    private class ShiftTimesProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("shiftStart")]
        public DateTime ShiftStart { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("shiftEnd")]
        public DateTime ShiftEnd { get; set; }
    }

    private class ShiftReadProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("taxiId")]
        public string TaxiId { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("shiftStart")]
        public DateTime ShiftStart { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("status")]
        public string Status { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("endMileage")]
        public int EndMileage { get; set; }
    }

    public class TaxiUnitProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("taxiId")]
        public string TaxiId { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("model")]
        public string Model { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("plateNumber")]
        public string PlateNumber { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("status")]
        public string Status { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("yearManufactured")]
        public int YearManufactured { get; set; }
    }

    private class EligibilityUserProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("licenseExpiryDate")]
        public DateTimeOffset? LicenseExpiryDate { get; set; }
    }

    private class EligibilityJobProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("status")]
        public string Status { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("dateLogged")]
        public DateTimeOffset DateLogged { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("estimatedCompletionDate")]
        public DateTimeOffset? EstimatedCompletionDate { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("scheduledDate")]
        public DateTimeOffset? ScheduledDate { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("issueTitle")]
        public string IssueTitle { get; set; }
    }

    private class UserProfileProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("assignedTaxiId")]
        public string AssignedTaxiId { get; set; }
    }

    public async Task<GarageGeofence> GetGarageGeofenceAsync()
    {
        var fallback = new GarageGeofence(ShiftRules.GarageLatitude, ShiftRules.GarageLongitude, ShiftRules.GarageRadiusMeters);
        try
        {
            var config = await CrossFirebaseFirestore.Current
                .GetCollection("system_configs")
                .GetDocument("global")
                .GetDocumentSnapshotAsync<GarageConfigProxy>();

            var data = config?.Data;
            if (data == null || data.GarageLatitude == 0 || data.GarageLongitude == 0)
            {
                return fallback;
            }

            double radius = data.GarageRadiusMeters > 0 ? data.GarageRadiusMeters : ShiftRules.GarageRadiusMeters;
            return new GarageGeofence(data.GarageLatitude, data.GarageLongitude, radius);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Garage Config Error: {ex.Message}");
            return fallback;
        }
    }

    public async Task<string> SubmitClockInRequestAsync(ClockInRequestSubmission request)
    {
        var user = CrossFirebaseAuth.Current.CurrentUser;
        if (user == null) throw new Exception("No authenticated driver found.");

        // No point asking the manager to approve a shift the rules wouldn't allow anyway.
        if (await GetClockInBlockReasonAsync(request.TaxiId) is string blocked)
        {
            throw new InvalidOperationException(blocked);
        }

        bool Passed(string item) => request.Inspection.TryGetValue(item, out bool ok) && ok;
        var proxy = new ClockInRequestProxy
        {
            DriverId = user.Uid,
            TaxiId = request.TaxiId,
            Status = "Pending",
            FlagReasons = request.FlagReasons,
            TireCondition = Passed("Tires"),
            UnderTheHood = Passed("Hood"),
            LightsCondition = Passed("Lights"),
            InteriorCleanliness = Passed("Interior"),
            ExteriorCondition = Passed("Exterior"),
            IsBelowHalfTank = request.IsBelowHalfTank,
            StartMileage = request.StartMileage,
            FuelDashboardUrl = request.FuelDashboardUrl ?? string.Empty,
            OdometerPhotoUrl = request.OdometerPhotoUrl ?? string.Empty,
            DefectReportIds = string.Join(",", request.DefectReportIds),
            CreatedAt = DateTime.UtcNow,
        };

        var doc = await CrossFirebaseFirestore.Current.GetCollection("clockin_requests").AddDocumentAsync(proxy);
        return doc.Id;
    }

    public async Task<ClockInRequestStatus?> GetClockInRequestAsync(string requestId)
    {
        try
        {
            var snapshot = await CrossFirebaseFirestore.Current
                .GetCollection("clockin_requests")
                .GetDocument(requestId)
                .GetDocumentSnapshotAsync<ClockInRequestProxy>();
            var data = snapshot?.Data;
            if (data == null) return null;

            return new ClockInRequestStatus
            {
                Status = data.Status ?? string.Empty,
                TaxiId = data.TaxiId ?? string.Empty,
                StartMileage = data.StartMileage,
                FlagReasons = data.FlagReasons ?? string.Empty,
                ManagerNote = data.ManagerNote,
                ShiftId = data.ShiftId,
                Checklist = new HandoverChecklistSubmission
                {
                    IsEndShift = false,
                    TireCondition = data.TireCondition,
                    UnderTheHood = data.UnderTheHood,
                    LightsCondition = data.LightsCondition,
                    InteriorCleanliness = data.InteriorCleanliness,
                    ExteriorCondition = data.ExteriorCondition,
                    IsBelowHalfTank = data.IsBelowHalfTank,
                    FuelDashboardUrl = string.IsNullOrEmpty(data.FuelDashboardUrl) ? null : data.FuelDashboardUrl,
                    OdometerPhotoUrl = string.IsNullOrEmpty(data.OdometerPhotoUrl) ? null : data.OdometerPhotoUrl,
                },
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Clock-in Request Read Error: {ex.Message}");
            return null;
        }
    }

    public Task CancelClockInRequestAsync(string requestId) =>
        CrossFirebaseFirestore.Current.GetCollection("clockin_requests").GetDocument(requestId)
            .UpdateDataAsync(new Dictionary<object, object> { { "status", "Cancelled" } });

    public Task MarkClockInRequestUsedAsync(string requestId, string shiftId) =>
        CrossFirebaseFirestore.Current.GetCollection("clockin_requests").GetDocument(requestId)
            .UpdateDataAsync(new Dictionary<object, object> { { "status", "ClockedIn" }, { "shiftId", shiftId } });

    private class ClockInRequestProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("driverId")] public string DriverId { get; set; }
        [Plugin.Firebase.Firestore.FirestoreProperty("taxiId")] public string TaxiId { get; set; }
        [Plugin.Firebase.Firestore.FirestoreProperty("status")] public string Status { get; set; }
        [Plugin.Firebase.Firestore.FirestoreProperty("flagReasons")] public string FlagReasons { get; set; }
        [Plugin.Firebase.Firestore.FirestoreProperty("tireCondition")] public bool TireCondition { get; set; }
        [Plugin.Firebase.Firestore.FirestoreProperty("underTheHood")] public bool UnderTheHood { get; set; }
        [Plugin.Firebase.Firestore.FirestoreProperty("lightsCondition")] public bool LightsCondition { get; set; }
        [Plugin.Firebase.Firestore.FirestoreProperty("interiorCleanliness")] public bool InteriorCleanliness { get; set; }
        [Plugin.Firebase.Firestore.FirestoreProperty("exteriorCondition")] public bool ExteriorCondition { get; set; }
        [Plugin.Firebase.Firestore.FirestoreProperty("isBelowHalfTank")] public bool IsBelowHalfTank { get; set; }
        [Plugin.Firebase.Firestore.FirestoreProperty("startMileage")] public int StartMileage { get; set; }
        [Plugin.Firebase.Firestore.FirestoreProperty("fuelDashboardUrl")] public string FuelDashboardUrl { get; set; }
        [Plugin.Firebase.Firestore.FirestoreProperty("odometerPhotoUrl")] public string OdometerPhotoUrl { get; set; }
        [Plugin.Firebase.Firestore.FirestoreProperty("defectReportIds")] public string DefectReportIds { get; set; }
        [Plugin.Firebase.Firestore.FirestoreProperty("createdAt")] public DateTime CreatedAt { get; set; }
        [Plugin.Firebase.Firestore.FirestoreProperty("managerNote")] public string ManagerNote { get; set; }
        [Plugin.Firebase.Firestore.FirestoreProperty("shiftId")] public string ShiftId { get; set; }
    }

    public async Task RefreshTestClockAsync()
    {
#if DEBUG
        try
        {
            var config = await CrossFirebaseFirestore.Current
                .GetCollection("system_configs")
                .GetDocument("global")
                .GetDocumentSnapshotAsync<GarageConfigProxy>();
            ShiftClock.SetPretendPhilippineTime(config?.Data?.TestClockPh);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Test Clock Error: {ex.Message}");
        }
#else
        await Task.CompletedTask;
#endif
    }

    private class GarageConfigProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("testClockPh")]
        public string TestClockPh { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("garageLatitude")]
        public double GarageLatitude { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("garageLongitude")]
        public double GarageLongitude { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("garageRadiusMeters")]
        public double GarageRadiusMeters { get; set; }
    }

    private class ShiftScheduleProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("taxiId")]
        public string TaxiId { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("status")]
        public string Status { get; set; }
    }

    private class HandoverChecklistProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("shiftId")]
        public string ShiftId { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("checklistType")]
        public string ChecklistType { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("tireCondition")]
        public bool TireCondition { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("oilLevel")]
        public bool OilLevel { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("coolantLevel")]
        public bool CoolantLevel { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("lightsCondition")]
        public bool LightsCondition { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("interiorCleanliness")]
        public bool InteriorCleanliness { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("exteriorScratches")]
        public bool ExteriorScratches { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("fuelVerification")]
        public string FuelVerification { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("fuelDashboardUrl")]
        public string FuelDashboardUrl { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("odometerPhotoUrl")]
        public string OdometerPhotoUrl { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("timestamp")]
        public DateTime Timestamp { get; set; }
    }

}