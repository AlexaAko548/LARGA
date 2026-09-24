using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Google.Cloud.Firestore;
using LARGA.SharedCore.Models.FuelVerification;
using LARGA.Shared.Models.Entities;
using Microsoft.Extensions.Logging;

namespace LARGA.SharedCore.Services;

/// <summary>
/// Backs the ManagerWeb Fuel Verification page: driver-submitted fuel_logs, cross-referenced
/// with shifts/users for driver + unit, and against each driver's own previous refuel so the
/// manager can eyeball distance-covered and km/L instead of judging cost/liters in isolation.
/// Same Lazy&lt;FirestoreDb&gt; deferred-credentials pattern as the other ManagerWeb services.
/// </summary>
public class FuelVerificationService
{
    private readonly Lazy<FirestoreDb> _dbLazy;
    private readonly ILogger<FuelVerificationService> _logger;

    private FirestoreDb Db => _dbLazy.Value;

    public FuelVerificationService(Lazy<FirestoreDb> dbLazy, ILogger<FuelVerificationService> logger)
    {
        _dbLazy = dbLazy;
        _logger = logger;
    }

    public async Task<FuelVerificationSnapshot> GetSnapshotAsync()
    {
        List<FuelLog> logs = await GetAllAsync<FuelLog>("fuel_logs");
        List<ShiftLog> shifts = await GetAllAsync<ShiftLog>("shifts");
        List<UserProfile> drivers = await GetAllAsync<UserProfile>("users");

        Dictionary<string, ShiftLog> shiftById = shifts
            .Where(s => !string.IsNullOrEmpty(s.DocumentId))
            .ToDictionary(s => s.DocumentId);
        Dictionary<string, string> driverNames = drivers.ToDictionary(d => d.UserId, d => d.FullName);

        // Per-shift refuel ordinal ("Refuel #1", "#2"...), oldest first, and each entry's
        // previous reading within that same shift - falling back to the shift's own starting
        // odometer for a driver's first refuel, since there's nothing earlier to compare to.
        var entries = new List<FuelLogEntry>();
        foreach (IGrouping<string, FuelLog> group in logs.GroupBy(l => l.ShiftId ?? string.Empty))
        {
            shiftById.TryGetValue(group.Key, out ShiftLog? shift);
            // Prefer the shift's driver; a log submitted without a real shift (mobile writes
            // UNKNOWN_SHIFT) still carries its own driverId, so fall back to that.
            string? shiftDriverId = shift?.DriverId;
            string driverName = "Unknown Driver";
            if (!string.IsNullOrEmpty(shiftDriverId) && driverNames.TryGetValue(shiftDriverId, out string? name))
            {
                driverName = name;
            }
            else
            {
                string? logDriverId = group.Select(l => l.DriverId).FirstOrDefault(id => !string.IsNullOrEmpty(id));
                if (!string.IsNullOrEmpty(logDriverId) && driverNames.TryGetValue(logDriverId, out string? logName))
                {
                    driverName = logName;
                }
            }
            string taxiId = shift?.TaxiId ?? "—";

            int ordinal = 1;
            int? previousReading = shift != null && shift.StartMileage > 0 ? shift.StartMileage : null;
            string previousLabel = "Shift start odometer";

            foreach (FuelLog log in group.OrderBy(l => l.ReceiptTimestamp ?? DateTime.MinValue))
            {
                entries.Add(new FuelLogEntry
                {
                    FuelId = log.FuelId,
                    ShiftId = log.ShiftId,
                    RefuelLabel = $"Refuel #{ordinal}",
                    DriverName = driverName,
                    TaxiId = taxiId,
                    Timestamp = log.ReceiptTimestamp,
                    Cost = log.FuelCost,
                    Liters = log.LitersRefueled,
                    OdometerReading = log.OdometerReading,
                    Status = log.VerificationStatus,
                    FuelStation = log.FuelStation,
                    ORNumber = log.ORNumber,
                    ReceiptImageUrl = log.ReceiptImageUrl,
                    OdometerPhotoUrl = log.OdometerPhotoUrl,
                    FuelLogDetails = log.FuelLogDetails,
                    PreviousOdometerReading = previousReading,
                    PreviousOdometerLabel = previousLabel,
                });

                ordinal++;
                previousReading = log.OdometerReading > 0 ? log.OdometerReading : previousReading;
                previousLabel = "Previous refuel odometer";
            }
        }

        entries = entries.OrderByDescending(e => e.Timestamp).ToList();

        return new FuelVerificationSnapshot { Entries = entries };
    }

    /// <summary>Everything the "Review Fuel Submission" modal needs, including an
    /// independent GPS-tracked distance for the leg between this refuel and the previous
    /// one (or shift start) to cross-check against the driver's claimed odometer distance.
    /// Fetched on demand per submission - see FuelReviewDetail's own doc comment for why
    /// this isn't part of the bulk list query.</summary>
    public async Task<FuelReviewDetail?> GetReviewDetailAsync(string fuelId)
    {
        DocumentSnapshot logSnapshot = await Db.Collection("fuel_logs").Document(fuelId).GetSnapshotAsync();
        if (!logSnapshot.Exists)
        {
            return null;
        }
        FuelLog log = logSnapshot.ConvertTo<FuelLog>();

        ShiftLog? shift = null;
        if (!string.IsNullOrEmpty(log.ShiftId))
        {
            DocumentSnapshot shiftSnapshot = await Db.Collection("shifts").Document(log.ShiftId).GetSnapshotAsync();
            if (shiftSnapshot.Exists)
            {
                shift = shiftSnapshot.ConvertTo<ShiftLog>();
            }
        }

        string driverName = "Unknown Driver";
        string? driverIdForName = !string.IsNullOrEmpty(shift?.DriverId) ? shift!.DriverId : log.DriverId;
        if (!string.IsNullOrEmpty(driverIdForName))
        {
            DocumentSnapshot driverSnapshot = await Db.Collection("users").Document(driverIdForName).GetSnapshotAsync();
            if (driverSnapshot.Exists)
            {
                driverName = driverSnapshot.ConvertTo<UserProfile>().FullName;
            }
        }

        // Same per-shift ordinal/previous-reading logic as GetSnapshotAsync, scoped to just
        // this one shift instead of every fuel_logs document.
        List<FuelLog> shiftLogs = await GetWhereEqualAsync<FuelLog>("fuel_logs", "shiftId", log.ShiftId);
        List<FuelLog> ordered = shiftLogs.OrderBy(l => l.ReceiptTimestamp ?? DateTime.MinValue).ToList();

        int ordinal = 1;
        int? previousReading = shift != null && shift.StartMileage > 0 ? shift.StartMileage : null;
        string previousLabel = "Shift start odometer";
        DateTime? windowStart = shift?.ShiftStart;

        foreach (FuelLog entry in ordered)
        {
            if (entry.FuelId == log.FuelId)
            {
                break;
            }
            ordinal++;
            previousReading = entry.OdometerReading > 0 ? entry.OdometerReading : previousReading;
            previousLabel = "Previous refuel odometer";
            windowStart = entry.ReceiptTimestamp ?? windowStart;
        }

        double? gpsDistanceKm = null;
        if (!string.IsNullOrEmpty(log.ShiftId) && windowStart.HasValue && log.ReceiptTimestamp.HasValue)
        {
            gpsDistanceKm = await TryGetGpsDistanceKmAsync(log.ShiftId, windowStart.Value, log.ReceiptTimestamp.Value);
        }

        return new FuelReviewDetail
        {
            FuelId = log.FuelId,
            Status = log.VerificationStatus,
            DriverName = driverName,
            TaxiId = shift?.TaxiId ?? "—",
            RefuelLabel = $"Refuel #{ordinal}",
            Timestamp = log.ReceiptTimestamp,
            Cost = log.FuelCost,
            Liters = log.LitersRefueled,
            OdometerReading = log.OdometerReading,
            FuelStation = log.FuelStation,
            ORNumber = log.ORNumber,
            ReceiptImageUrl = log.ReceiptImageUrl,
            OdometerPhotoUrl = log.OdometerPhotoUrl,
            IsCostManuallyEdited = log.IsCostManuallyEdited,
            IsQuantityManuallyEdited = log.IsQuantityManuallyEdited,
            IsFuelStationManuallyEdited = log.IsFuelStationManuallyEdited,
            IsReceiptDateManuallyEdited = log.IsReceiptDateManuallyEdited,
            PreviousOdometerReading = previousReading,
            PreviousOdometerLabel = previousLabel,
            GpsDistanceKm = gpsDistanceKm,
        };
    }

    /// <summary>Sums haversine distance between consecutive gps_telemetry pings for a shift
    /// within [windowStart, windowEnd]. Returns null (rather than 0) on anything short of a
    /// clean two-plus-point read - no telemetry, one lone ping, or a missing composite index
    /// (shiftId Asc, timestamp Asc - same gotcha FleetReportingService.IsMovingAsync already
    /// documents for its own gps_telemetry query; Firestore's exception on first run names
    /// the exact console link to create it) - so the modal shows "no GPS data" instead of a
    /// false "0 km driven, way off".</summary>
    private async Task<double?> TryGetGpsDistanceKmAsync(string shiftId, DateTime windowStart, DateTime windowEnd)
    {
        try
        {
            QuerySnapshot snapshot = await Db.Collection("gps_telemetry")
                .WhereEqualTo("shiftId", shiftId)
                .WhereGreaterThanOrEqualTo("timestamp", windowStart)
                .WhereLessThanOrEqualTo("timestamp", windowEnd)
                .OrderBy("timestamp")
                .GetSnapshotAsync();

            List<GpsTelemetry> points = snapshot.Documents.Select(d => d.ConvertTo<GpsTelemetry>()).ToList();
            if (points.Count < 2)
            {
                return null;
            }

            double totalKm = 0;
            for (int i = 1; i < points.Count; i++)
            {
                totalKm += HaversineKm(points[i - 1].Latitude, points[i - 1].Longitude, points[i].Latitude, points[i].Longitude);
            }
            return Math.Round(totalKm, 1);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GPS distance unavailable for shift {ShiftId}", shiftId);
            return null;
        }
    }

    private static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthRadiusKm = 6371.0;
        double dLat = DegreesToRadians(lat2 - lat1);
        double dLon = DegreesToRadians(lon2 - lon1);
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                   + Math.Cos(DegreesToRadians(lat1)) * Math.Cos(DegreesToRadians(lat2))
                   * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return earthRadiusKm * c;
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;

    public async Task<FuelActionResult> VerifyAsync(string fuelId)
    {
        try
        {
            await Db.Collection("fuel_logs").Document(fuelId)
                .UpdateAsync("verificationStatus", new FuelVerificationStatusConverter().ToFirestore(FuelVerificationStatus.Verified));
            return new FuelActionResult { Ok = true };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to verify fuel log {FuelId}", fuelId);
            return new FuelActionResult { Ok = false, ErrorMessage = "Could not verify this submission. Please try again." };
        }
    }

    public async Task<FuelActionResult> FlagAsync(string fuelId, string? reason)
    {
        try
        {
            var updates = new Dictionary<string, object>
            {
                ["verificationStatus"] = new FuelVerificationStatusConverter().ToFirestore(FuelVerificationStatus.Flagged),
            };
            if (!string.IsNullOrWhiteSpace(reason))
            {
                updates["fuelLogDetails"] = reason;
            }
            await Db.Collection("fuel_logs").Document(fuelId).UpdateAsync(updates);
            return new FuelActionResult { Ok = true };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to flag fuel log {FuelId}", fuelId);
            return new FuelActionResult { Ok = false, ErrorMessage = "Could not flag this submission. Please try again." };
        }
    }

    /// <summary>Closes out a Flagged submission: records how it was resolved (driver
    /// explanation, penalty applied, etc.) alongside the original flag reason for an audit
    /// trail, and moves it to Verified - it's been looked at and dealt with, not still an
    /// open question, so it belongs in the Verified queue rather than staying Flagged or
    /// going back to unreviewed Pending.</summary>
    public async Task<FuelActionResult> ResolveFlagAsync(string fuelId, string resolutionNote)
    {
        try
        {
            DocumentSnapshot snapshot = await Db.Collection("fuel_logs").Document(fuelId).GetSnapshotAsync();
            string existingReason = snapshot.Exists ? snapshot.ConvertTo<FuelLog>().FuelLogDetails ?? string.Empty : string.Empty;

            string combinedNote = string.IsNullOrWhiteSpace(existingReason)
                ? $"Resolved: {resolutionNote}"
                : $"{existingReason}\n\nResolved: {resolutionNote}";

            var updates = new Dictionary<string, object>
            {
                ["verificationStatus"] = new FuelVerificationStatusConverter().ToFirestore(FuelVerificationStatus.Verified),
                ["fuelLogDetails"] = combinedNote,
            };
            await Db.Collection("fuel_logs").Document(fuelId).UpdateAsync(updates);
            return new FuelActionResult { Ok = true };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve flag on fuel log {FuelId}", fuelId);
            return new FuelActionResult { Ok = false, ErrorMessage = "Could not resolve this flag. Please try again." };
        }
    }

    /// <summary>Resets a Verified/Flagged submission back to Pending, in case a manager
    /// acted on it by mistake.</summary>
    public async Task<FuelActionResult> ResetToPendingAsync(string fuelId)
    {
        try
        {
            await Db.Collection("fuel_logs").Document(fuelId)
                .UpdateAsync("verificationStatus", new FuelVerificationStatusConverter().ToFirestore(FuelVerificationStatus.Pending));
            return new FuelActionResult { Ok = true };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to reset fuel log {FuelId} to pending", fuelId);
            return new FuelActionResult { Ok = false, ErrorMessage = "Could not update this submission. Please try again." };
        }
    }

    private async Task<List<T>> GetAllAsync<T>(string collection) where T : class
    {
        QuerySnapshot snapshot = await Db.Collection(collection).GetSnapshotAsync();
        return ConvertDocuments<T>(snapshot, collection);
    }

    private async Task<List<T>> GetWhereEqualAsync<T>(string collection, string field, object value) where T : class
    {
        QuerySnapshot snapshot = await Db.Collection(collection).WhereEqualTo(field, value).GetSnapshotAsync();
        return ConvertDocuments<T>(snapshot, collection);
    }

    private List<T> ConvertDocuments<T>(QuerySnapshot snapshot, string collection) where T : class
    {
        var results = new List<T>(snapshot.Documents.Count);
        foreach (DocumentSnapshot doc in snapshot.Documents)
        {
            try
            {
                results.Add(doc.ConvertTo<T>());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping {Collection}/{DocumentId}: failed to convert to {Type}",
                    collection, doc.Id, typeof(T).Name);
            }
        }
        return results;
    }
}
