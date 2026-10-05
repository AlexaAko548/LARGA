using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Google.Cloud.Firestore;
using LARGA.Shared.Models.Entities;
using Microsoft.Extensions.Logging;

namespace LARGA.SharedCore.Services;

/// <summary>
/// Enforces the end of the operating day (ShiftRules) on shifts drivers left open. Run
/// periodically by ManagerWeb's background monitor:
/// - 10:00 PM: a shift still open past its return time raises one "unit not returned" alert
///   - the owner asked for exactly this (interview Q19), since staff are often asleep when
///   drivers come back late and late returns get overlooked;
/// - 6:00 AM the next day: a shift still open is closed as "Auto-Closed" (missed clock-out),
///   with a manager alert. That stops it counting as on shift (and keeps GPS tracking bound to
///   real shifts, per the paper's auto-cutoff). Its end odometer is filled from the driver's
///   next clock-in; any late fee is the manager's call, since the real return time is unknown.
/// Same Lazy&lt;FirestoreDb&gt; pattern as the other ManagerWeb services.
/// </summary>
public class ShiftDeadlineService
{
    public const string LateReturnAlertType = "LateReturn";
    public const string MissedClockOutAlertType = "MissedClockOut";

    private readonly Lazy<FirestoreDb> _dbLazy;
    private readonly ILogger<ShiftDeadlineService> _logger;

    private FirestoreDb Db => _dbLazy.Value;

    public ShiftDeadlineService(Lazy<FirestoreDb> dbLazy, ILogger<ShiftDeadlineService> logger)
    {
        _dbLazy = dbLazy;
        _logger = logger;
    }

    public async Task ProcessOpenShiftsAsync()
    {
        DateTime now = DateTime.UtcNow;
        QuerySnapshot open = await Db.Collection("shifts").WhereEqualTo("status", "Active").GetSnapshotAsync();
        if (open.Count == 0)
        {
            return;
        }

        Dictionary<string, string> driverNames = (await Db.Collection("users").GetSnapshotAsync()).Documents
            .ToDictionary(d => d.Id, d => d.TryGetValue("fullName", out string name) ? name : d.Id);

        foreach (DocumentSnapshot doc in open.Documents)
        {
            ShiftLog shift;
            try
            {
                shift = doc.ConvertTo<ShiftLog>();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping shifts/{Id}: unreadable", doc.Id);
                continue;
            }

            if (shift.ShiftStart is not DateTime start)
            {
                continue;
            }

            string driverName = driverNames.TryGetValue(shift.DriverId, out string? n) ? n : "A driver";
            string due = ShiftRules.ReturnDeadlineUtc(start).ToPhilippineTime().ToString("h:mm tt");

            try
            {
                if (now >= ShiftRules.AutoCloseAtUtc(start))
                {
                    await doc.Reference.UpdateAsync(new Dictionary<string, object>
                    {
                        ["status"] = ShiftManagementService.AutoClosedStatus,
                        ["isOnBreak"] = false,
                        ["managerNote"] = "Auto-closed: the driver never clocked out. Return time unknown - review and add any late fee from the Financial Ledger.",
                    });

                    await RaiseOnceAsync($"{doc.Id}_MISSED", MissedClockOutAlertType, shift, driverName,
                        $"{driverName} never clocked out of {shift.TaxiId} (shift of {start.ToPhilippineTime():MMM d}). The shift was auto-closed - check when the unit was actually returned.");
                }
                else if (now >= ShiftRules.ReturnDeadlineUtc(start))
                {
                    await RaiseOnceAsync($"{doc.Id}_LATE", LateReturnAlertType, shift, driverName,
                        $"{driverName} hasn't returned {shift.TaxiId} yet (due {due}). Late fee applies from 10:30 PM.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to process deadline for shift {ShiftId}", doc.Id);
            }
        }
    }

    // Deterministic IDs make this safe to run every few minutes: each alert is raised once
    // per shift, same approach as AlertService.CreateIdleAlertIfNeededAsync.
    private async Task RaiseOnceAsync(string alertId, string type, ShiftLog shift, string driverName, string message)
    {
        DocumentReference docRef = Db.Collection("system_alerts").Document(alertId);
        if ((await docRef.GetSnapshotAsync()).Exists)
        {
            return;
        }

        await docRef.SetAsync(new SystemAlert
        {
            Type = type,
            DriverId = shift.DriverId,
            DriverName = driverName,
            TaxiId = shift.TaxiId,
            UnitLabel = shift.TaxiId,
            ShiftId = shift.DocumentId,
            Message = message,
            Timestamp = DateTime.UtcNow,
            IsRead = false,
        });
    }
}
