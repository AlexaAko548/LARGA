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

        var shiftProxy = new ShiftLogProxy
        {
            DriverId = user.Uid,
            TaxiId = taxiId,
            ShiftStart = now,
            StartMileage = startMileage,
            Status = "Active",
            ShiftId = string.Empty,
            IsOnBreak = false,
            ManagerNote = ""
        };

        var documentReference = await CrossFirebaseFirestore.Current
            .GetCollection("shifts")
            .AddDocumentAsync(shiftProxy);

        // Everything else that points at a shift (fuel_logs, handover_checklists,
        // boundary_payments, ManagerWeb's lookups) uses the shiftId field and the document ID
        // interchangeably, so they must be the same value. The ID only exists once the
        // document does, hence the follow-up write.
        try
        {
            await documentReference.UpdateDataAsync(new Dictionary<object, object> { { "shiftId", documentReference.Id } });
        }
        catch (Exception ex)
        {
            // The shift itself is started - ManagerWeb falls back to the document ID when
            // shiftId is missing, so this isn't worth failing the clock-in over.
            System.Diagnostics.Debug.WriteLine($"Shift Id Backfill Error: {ex.Message}");
        }

        return documentReference.Id;
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

    /// <summary>Ends the shift and records its late-return fee (ShiftRules: the unit is timed on
    /// return, i.e. now) and its low-fuel penalty (below half-tank at return). Returns both so
    /// the Shift Completed screen can show today's total.</summary>
    public async Task<ShiftEndCharges> ClockOutAsync(string activeShiftId, int endMileage, bool fuelBelowHalf, string managerNote = "")
    {
        try
        {
            await RefreshTestClockAsync();
            DateTime now = ShiftClock.UtcNow;
            var shiftDoc = CrossFirebaseFirestore.Current.GetCollection("shifts").GetDocument(activeShiftId);

            var updateData = new Dictionary<object, object>
            {
                { "shiftEnd", now },
                { "endMileage", endMileage },
                { "status", "Completed" },
                { "managerNote", managerNote },
                { "isOnBreak", false },
            };

            decimal fuelPenalty = fuelBelowHalf ? ShiftRules.LowFuelPenalty : 0m;
            updateData["fuelPenalty"] = (double)fuelPenalty;

            decimal lateFee = 0m;
            try
            {
                var snapshot = await shiftDoc.GetDocumentSnapshotAsync<ShiftReadProxy>();
                if (snapshot?.Data != null)
                {
                    lateFee = ShiftRules.LateReturnFee(FixPluginDate(snapshot.Data.ShiftStart), now);
                    updateData["lateFee"] = (double)lateFee;
                }
            }
            catch (Exception ex)
            {
                // Without the start time there's no fee to work out; the shift still ends, and
                // the manager can add a late fee from the ledger if needed.
                System.Diagnostics.Debug.WriteLine($"Late Fee Error: {ex.Message}");
            }

            await shiftDoc.UpdateDataAsync(updateData);
            return new ShiftEndCharges(lateFee, fuelPenalty);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Service Error: {ex.Message}");
            throw;
        }
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

    private class ShiftLogProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("driverId")]
        public string DriverId { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("taxiId")]
        public string TaxiId { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("shiftStart")]
        public DateTime ShiftStart { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("startMileage")]
        public int StartMileage { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("status")]
        public string Status { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("shiftId")]
        public string ShiftId { get; set; }

        // ADDED: Missing fields for initial clock-in
        [Plugin.Firebase.Firestore.FirestoreProperty("isOnBreak")]
        public bool IsOnBreak { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("managerNote")]
        public string ManagerNote { get; set; }
    }
}