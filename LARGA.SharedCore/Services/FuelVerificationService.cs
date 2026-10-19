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
        QuerySnapshot logsSnapshot = await Db.Collection("fuel_logs").GetSnapshotAsync();
        return await BuildSnapshotAsync(logsSnapshot);
    }

    /// <summary>Live-updating version of GetSnapshotAsync for the Fuel Verification page -
    /// fires immediately with the current state, then again on every fuel_logs write (new
    /// submission, verify/flag/resolve/reset) so the Pending/Verified/Flagged tabs never need
    /// a manual reload. Stop the returned listener (StopAsync) when the page is disposed.</summary>
    public FirestoreChangeListener Listen(Action<FuelVerificationSnapshot> onSnapshot)
    {
        return Db.Collection("fuel_logs").Listen(snapshot =>
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    FuelVerificationSnapshot result = await BuildSnapshotAsync(snapshot);
                    onSnapshot(result);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to rebuild fuel verification snapshot from live update");
                }
            });
        });
    }

    private async Task<FuelVerificationSnapshot> BuildSnapshotAsync(QuerySnapshot logsSnapshot)
    {
        List<FuelLog> logs = ConvertDocuments<FuelLog>(logsSnapshot, "fuel_logs");
        // Only the shifts these receipts belong to, not every shift ever (one batched read).
        List<ShiftLog> shifts = await GetShiftsByIdAsync(logs.Select(l => l.ShiftId));
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
            // A real shift's driver applies to every log filed under it. A log with no real
            // shift (mobile falls back to the placeholder "UNKNOWN_SHIFT" when it can't find
            // an active shift) has no shift-level driver to borrow, so it must resolve its
            // OWN driverId instead - multiple different drivers' logs can land in this same
            // placeholder group, and crediting the whole group to whichever one happened to
            // come first misattributes every other driver's submission in it.
            string? shiftDriverId = shift?.DriverId;
            string? groupDriverName = !string.IsNullOrEmpty(shiftDriverId) && driverNames.TryGetValue(shiftDriverId, out string? name)
                ? name
                : null;
            string taxiId = shift?.TaxiId ?? "—";

            int ordinal = 1;
            int? previousReading = shift != null && shift.StartMileage > 0 ? shift.StartMileage : null;
            string previousLabel = "Shift start odometer";

            foreach (FuelLog log in group.OrderBy(EventTime))
            {
                string driverName = groupDriverName
                    ?? (!string.IsNullOrEmpty(log.DriverId) && driverNames.TryGetValue(log.DriverId, out string? logName) ? logName : "Unknown Driver");

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
                    IsAutoFlagged = log.AutoFlagged,
                    SubmittedAt = log.SubmittedAt,
                    VerifiedAt = log.VerifiedAt,
                    FlaggedAt = log.FlaggedAt,
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
        List<FuelLog> ordered = shiftLogs.OrderBy(EventTime).ToList();

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
            windowStart = (entry.SubmittedAt ?? entry.ReceiptTimestamp) ?? windowStart;
        }

        // The GPS leg ends when the report was sent (server time, with the odometer photo taken
        // then) - receiptTimestamp is only the receipt's printed date, at midnight.
        double? gpsDistanceKm = null;
        DateTime? windowEnd = log.SubmittedAt ?? log.ReceiptTimestamp;
        if (!string.IsNullOrEmpty(log.ShiftId) && windowStart.HasValue && windowEnd.HasValue)
        {
            gpsDistanceKm = await TryGetGpsDistanceKmAsync(log.ShiftId, windowStart.Value, windowEnd.Value);
        }

        return new FuelReviewDetail
        {
            FuelId = log.FuelId,
            Status = log.VerificationStatus,
            DriverName = driverName,
            TaxiId = shift?.TaxiId ?? "—",
            RefuelLabel = $"Refuel #{ordinal}",
            Timestamp = log.ReceiptTimestamp,
            SubmittedAt = log.SubmittedAt,
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
            IsOrNumberManuallyEdited = log.IsOrNumberManuallyEdited,
            PreviousOdometerReading = previousReading,
            PreviousOdometerLabel = previousLabel,
            GpsDistanceKm = gpsDistanceKm,
            Tolerances = await GetTolerancesAsync(),
        };
    }

    /// <summary>When a fuel log happened, for ordering a shift's refuels: its server submission
    /// time, else (older reports) the receipt date.</summary>
    private static DateTime EventTime(FuelLog log) => log.SubmittedAt ?? log.ReceiptTimestamp ?? DateTime.MinValue;

    /// <summary>FUEL-017 limits from system_configs/global (defaults when it can't be read).</summary>
    public async Task<FuelTolerances> GetTolerancesAsync()
    {
        try
        {
            DocumentSnapshot config = await Db.Collection("system_configs").Document("global").GetSnapshotAsync();
            return FuelTolerances.From(config.Exists ? config.ConvertTo<SystemConfig>() : null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fuel tolerances unreadable - using the defaults");
            return FuelTolerances.Default;
        }
    }

    // ---------------------------------------------------------------------
    // FUEL-017 automatic check (FuelAutoCheckService) and FUEL-004 bell alerts
    // ---------------------------------------------------------------------

    public const string AutoFlaggedAction = "FuelAutoFlagged";
    public const string FuelAlertType = "FuelDiscrepancy";

    public static string FuelAlertId(string fuelId) => $"fuel_{fuelId}";

    /// <summary>Whether this log still needs the automatic pass: an unchecked Pending submission
    /// (odometer vs GPS check), or a log the phone flagged itself (FUEL-004 station) whose bell
    /// alert hasn't been raised yet.</summary>
    public static bool NeedsAutoCheck(FuelLog log) =>
        (log.VerificationStatus == FuelVerificationStatus.Pending && log.AutoCheckedAt is null)
        || (log.VerificationStatus == FuelVerificationStatus.Flagged && log.AutoFlagged && log.AutoFlagAlertAt is null);

    /// <summary>
    /// The automatic pass over one submission. A Pending one whose odometer distance is off from GPS
    /// (FuelReviewDetail.AutoFlagReason) is set Flagged with the readable reason, logged as FuelAutoFlagged and
    /// raised in the bell; one within tolerance - or with no GPS data to compare - is only marked
    /// checked, and stays Pending for the manager. A phone-flagged one (FUEL-004) just gets its
    /// bell alert. The manager can still verify or keep any of them flagged as usual.
    /// </summary>
    public async Task AutoCheckAsync(string fuelId)
    {
        DocumentReference docRef = Db.Collection("fuel_logs").Document(fuelId);
        DocumentSnapshot first = await docRef.GetSnapshotAsync();
        if (!first.Exists)
        {
            return;
        }

        FuelLog current = first.ConvertTo<FuelLog>();
        if (!NeedsAutoCheck(current))
        {
            return;
        }

        FuelReviewDetail? detail = await GetReviewDetailAsync(fuelId);
        if (detail is null)
        {
            return;
        }

        string? autoFlagReason = current.VerificationStatus == FuelVerificationStatus.Pending
            ? detail.AutoFlagReason()
            : null;
        DateTime now = DateTime.UtcNow;

        await Db.RunTransactionAsync(async transaction =>
        {
            // Re-read: a manager may have acted on it while the GPS distance was being worked out.
            DocumentSnapshot snapshot = await transaction.GetSnapshotAsync(docRef);
            if (!snapshot.Exists)
            {
                return;
            }

            FuelLog log = snapshot.ConvertTo<FuelLog>();
            if (!NeedsAutoCheck(log))
            {
                return;
            }

            if (log.VerificationStatus == FuelVerificationStatus.Flagged)
            {
                // FUEL-004: flagged by the phone on submit - bell alert only.
                transaction.Set(Db.Collection("system_alerts").Document(FuelAlertId(fuelId)), FuelAlert(detail, log.FuelLogDetails, now));
                transaction.Update(docRef, "autoFlagAlertAt", now);
                return;
            }

            if (log.VerificationStatus != FuelVerificationStatus.Pending)
            {
                return;
            }

            if (autoFlagReason is null)
            {
                transaction.Update(docRef, "autoCheckedAt", now);
                return;
            }

            string reason = autoFlagReason;
            transaction.Update(docRef, new Dictionary<string, object>
            {
                ["verificationStatus"] = new FuelVerificationStatusConverter().ToFirestore(FuelVerificationStatus.Flagged),
                ["fuelLogDetails"] = reason,
                ["autoFlagged"] = true,
                ["flaggedAt"] = now,
                ["autoCheckedAt"] = now,
                ["autoFlagAlertAt"] = now,
            });
            transaction.Create(Db.Collection("audit_logs").Document(), new AuditLog
            {
                UserId = string.Empty, // System
                ActionType = AutoFlaggedAction,
                AuditLogDetails = $"Automatically flagged the fuel submission for shift {log.ShiftId} ({detail.DriverName}, {detail.TaxiId}): {reason}.",
                Timestamp = now,
            });
            transaction.Set(Db.Collection("system_alerts").Document(FuelAlertId(fuelId)), FuelAlert(detail, reason, now));
        });
    }

    private static SystemAlert FuelAlert(FuelReviewDetail detail, string? reason, DateTime now) => new()
    {
        Type = FuelAlertType,
        DriverName = detail.DriverName,
        TaxiId = detail.TaxiId,
        UnitLabel = detail.TaxiId,
        Message = $"Fuel submission flagged - {detail.DriverName} ({detail.TaxiId}): {(string.IsNullOrWhiteSpace(reason) ? "needs review" : reason)}.",
        Timestamp = now,
        IsRead = false,
    };

    /// <summary>For the audit entry of a manager's decision on a submission the system flagged:
    /// " (auto-flagged: reason)", else empty.</summary>
    private static string AutoFlagNote(DocumentSnapshot snapshot)
    {
        if (!snapshot.Exists || !snapshot.TryGetValue("autoFlagged", out bool autoFlagged) || !autoFlagged)
        {
            return string.Empty;
        }

        string reason = snapshot.TryGetValue("fuelLogDetails", out string? details) ? details ?? string.Empty : string.Empty;
        return string.IsNullOrWhiteSpace(reason) ? " (auto-flagged)" : $" (auto-flagged: {reason.Split("\n\n")[0]})";
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

    public async Task<FuelActionResult> VerifyAsync(string fuelId, string? actorUserId = null)
    {
        try
        {
            DocumentReference docRef = Db.Collection("fuel_logs").Document(fuelId);
            DocumentSnapshot snapshot = await docRef.GetSnapshotAsync();
            string shiftId = snapshot.Exists && snapshot.TryGetValue("shiftId", out string? sid) ? sid : "unknown shift";

            var updates = new Dictionary<string, object>
            {
                ["verificationStatus"] = new FuelVerificationStatusConverter().ToFirestore(FuelVerificationStatus.Verified),
                ["verifiedAt"] = DateTime.UtcNow,
            };

            WriteBatch batch = Db.StartBatch();
            batch.Update(docRef, updates);
            batch.Create(Db.Collection("audit_logs").Document(), new AuditLog
            {
                UserId = actorUserId ?? string.Empty,
                ActionType = "FuelVerified",
                AuditLogDetails = $"Verified the fuel submission for shift {shiftId}{AutoFlagNote(snapshot)}.",
                Timestamp = DateTime.UtcNow,
            });
            await batch.CommitAsync();
            return new FuelActionResult { Ok = true };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to verify fuel log {FuelId}", fuelId);
            return new FuelActionResult { Ok = false, ErrorMessage = "Could not verify this submission. Please try again." };
        }
    }

    public async Task<FuelActionResult> FlagAsync(string fuelId, string? reason, string? actorUserId = null)
    {
        try
        {
            DocumentReference docRef = Db.Collection("fuel_logs").Document(fuelId);
            DocumentSnapshot snapshot = await docRef.GetSnapshotAsync();
            string shiftId = snapshot.Exists && snapshot.TryGetValue("shiftId", out string? sid) ? sid : "unknown shift";

            var updates = new Dictionary<string, object>
            {
                ["verificationStatus"] = new FuelVerificationStatusConverter().ToFirestore(FuelVerificationStatus.Flagged),
                ["flaggedAt"] = DateTime.UtcNow,
            };
            if (!string.IsNullOrWhiteSpace(reason))
            {
                updates["fuelLogDetails"] = reason;
            }

            WriteBatch batch = Db.StartBatch();
            batch.Update(docRef, updates);
            batch.Create(Db.Collection("audit_logs").Document(), new AuditLog
            {
                UserId = actorUserId ?? string.Empty,
                ActionType = "FuelFlagged",
                AuditLogDetails = string.IsNullOrWhiteSpace(reason)
                    ? $"Flagged the fuel submission for shift {shiftId}{AutoFlagNote(snapshot)}."
                    : $"Flagged the fuel submission for shift {shiftId}{AutoFlagNote(snapshot)}: {reason}",
                Timestamp = DateTime.UtcNow,
            });
            await batch.CommitAsync();
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
    public async Task<FuelActionResult> ResolveFlagAsync(string fuelId, string resolutionNote, string? actorUserId = null)
    {
        try
        {
            DocumentReference docRef = Db.Collection("fuel_logs").Document(fuelId);
            DocumentSnapshot snapshot = await docRef.GetSnapshotAsync();
            FuelLog record = snapshot.Exists ? snapshot.ConvertTo<FuelLog>() : new FuelLog();
            string existingReason = record.FuelLogDetails ?? string.Empty;

            string combinedNote = string.IsNullOrWhiteSpace(existingReason)
                ? $"Resolved: {resolutionNote}"
                : $"{existingReason}\n\nResolved: {resolutionNote}";

            var updates = new Dictionary<string, object>
            {
                ["verificationStatus"] = new FuelVerificationStatusConverter().ToFirestore(FuelVerificationStatus.Verified),
                ["fuelLogDetails"] = combinedNote,
                ["verifiedAt"] = DateTime.UtcNow,
            };

            WriteBatch batch = Db.StartBatch();
            batch.Update(docRef, updates);
            batch.Create(Db.Collection("audit_logs").Document(), new AuditLog
            {
                UserId = actorUserId ?? string.Empty,
                ActionType = "FuelFlagResolved",
                AuditLogDetails = $"Resolved the flag on shift {record.ShiftId}'s fuel submission{AutoFlagNote(snapshot)}: {resolutionNote}",
                Timestamp = DateTime.UtcNow,
            });
            await batch.CommitAsync();
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
    public async Task<FuelActionResult> ResetToPendingAsync(string fuelId, string? actorUserId = null)
    {
        try
        {
            DocumentReference docRef = Db.Collection("fuel_logs").Document(fuelId);
            DocumentSnapshot snapshot = await docRef.GetSnapshotAsync();
            string shiftId = snapshot.Exists && snapshot.TryGetValue("shiftId", out string? sid) ? sid : "unknown shift";

            WriteBatch batch = Db.StartBatch();
            batch.Update(docRef, new Dictionary<string, object>
            {
                ["verificationStatus"] = new FuelVerificationStatusConverter().ToFirestore(FuelVerificationStatus.Pending),
            });
            batch.Create(Db.Collection("audit_logs").Document(), new AuditLog
            {
                UserId = actorUserId ?? string.Empty,
                ActionType = "FuelResetToPending",
                AuditLogDetails = $"Reset the fuel submission for shift {shiftId} back to Pending.",
                Timestamp = DateTime.UtcNow,
            });
            await batch.CommitAsync();
            return new FuelActionResult { Ok = true };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to reset fuel log {FuelId} to pending", fuelId);
            return new FuelActionResult { Ok = false, ErrorMessage = "Could not update this submission. Please try again." };
        }
    }

    /// <summary>The shifts with these document IDs that exist. Blank IDs, and placeholders the
    /// phone writes when it has no shift (e.g. "UNKNOWN_SHIFT"), simply aren't found.</summary>
    private async Task<List<ShiftLog>> GetShiftsByIdAsync(IEnumerable<string?> shiftIds)
    {
        List<DocumentReference> refs = shiftIds
            .Where(id => !string.IsNullOrWhiteSpace(id) && !id.Contains('/'))
            .Distinct()
            .Select(id => Db.Collection("shifts").Document(id!))
            .ToList();
        if (refs.Count == 0)
        {
            return new List<ShiftLog>();
        }

        IList<DocumentSnapshot> snapshots = await Db.GetAllSnapshotsAsync(refs);
        var shifts = new List<ShiftLog>(snapshots.Count);
        foreach (DocumentSnapshot snapshot in snapshots.Where(s => s.Exists))
        {
            try
            {
                shifts.Add(snapshot.ConvertTo<ShiftLog>());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping shifts/{Id}: unreadable", snapshot.Id);
            }
        }
        return shifts;
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
