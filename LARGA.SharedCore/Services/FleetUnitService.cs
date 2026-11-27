using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Google.Cloud.Firestore;
using LARGA.Shared.Models.Entities;
using LARGA.SharedCore.Models.Fleet;
using Microsoft.Extensions.Logging;

namespace LARGA.SharedCore.Services;

/// <summary>
/// ManagerWeb's Fleet Units page: list, add, edit, delete / retire and restore taxis/{taxiId}. Every
/// change is Manager-only, checked here as well as on the page (the Admin SDK skips firestore.rules),
/// and writes an audit entry in the same transaction.
/// </summary>
public class FleetUnitService
{
    private const string ManagerOnlyMessage = "Only the Manager can change taxi units.";

    /// <summary>The IDs of units removed completely, so they're never given to another vehicle (audit
    /// entries still name them). Server-only: firestore.rules grants clients nothing here.</summary>
    public const string DeletedIdsCollection = "deleted_taxi_ids";

    private readonly Lazy<FirestoreDb> _dbLazy;
    private readonly ILogger<FleetUnitService> _logger;

    private FirestoreDb Db => _dbLazy.Value;
    private CollectionReference Taxis => Db.Collection("taxis");

    public FleetUnitService(Lazy<FirestoreDb> dbLazy, ILogger<FleetUnitService> logger)
    {
        _dbLazy = dbLazy;
        _logger = logger;
    }

    /// <summary>Every unit, retired ones included, by ID.</summary>
    public async Task<List<FleetUnitEntry>> GetUnitsAsync()
    {
        QuerySnapshot taxis = await Taxis.GetSnapshotAsync();
        QuerySnapshot users = await Db.Collection("users").GetSnapshotAsync();
        QuerySnapshot recentShifts = await Db.Collection("shifts")
            .WhereGreaterThanOrEqualTo("shiftStart", DateTime.UtcNow.AddDays(-30))
            .GetSnapshotAsync();
        PerfLog.Reads("taxis", taxis.Count);
        PerfLog.Reads("users", users.Count);
        PerfLog.Reads("shifts", recentShifts.Count);

        Dictionary<string, int> odometer = LatestOdometerByUnit(recentShifts.Documents);
        ILookup<string, string> drivers = users.Documents
            .Where(u => StaffRoles.IsDriver(Text(u, "role")) && Text(u, "assignedTaxiId").Length > 0)
            .ToLookup(u => Text(u, "assignedTaxiId"), u => Text(u, "fullName"), StringComparer.OrdinalIgnoreCase);

        var units = new List<FleetUnitEntry>();
        foreach (DocumentSnapshot doc in taxis.Documents)
        {
            TaxiUnit taxi;
            try
            {
                taxi = doc.ConvertTo<TaxiUnit>();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping taxis/{TaxiId}: unreadable", doc.Id);
                continue;
            }

            units.Add(new FleetUnitEntry
            {
                TaxiId = doc.Id,
                PlateNumber = taxi.PlateNumber,
                Model = taxi.Model,
                YearManufactured = taxi.YearManufactured,
                RecordedMileage = taxi.CurrentMileage,
                CurrentMileage = Math.Max(taxi.CurrentMileage, odometer.GetValueOrDefault(doc.Id)),
                Status = taxi.Status,
                IsRetired = TaxiStatusRules.IsRetired(taxi.Status),
                AssignedDriverName = drivers[doc.Id].Any() ? string.Join(", ", drivers[doc.Id]) : null,
            });
        }

        return units.OrderBy(u => u.TaxiId, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<FleetActionResult> AddUnitAsync(FleetUnitInput input, string actorUserId)
    {
        if (Validate(input) is string error)
        {
            return FleetActionResult.Fail(error);
        }

        return await RunAsync("add a unit", actorUserId, async transaction =>
        {
            QuerySnapshot all = await transaction.GetSnapshotAsync(Taxis);
            string plate = FleetUnitRules.NormalizePlate(input.PlateNumber);
            if (PlateOwner(all, plate, exceptTaxiId: null) is string owner)
            {
                return FleetActionResult.Fail(owner);
            }

            QuerySnapshot deleted = await transaction.GetSnapshotAsync(Db.Collection(DeletedIdsCollection));
            string taxiId = FleetUnitRules.NextTaxiId(all.Documents.Concat(deleted.Documents).Select(d => d.Id));
            // Create, not Set: never overwrites a unit that appeared meanwhile.
            transaction.Create(Taxis.Document(taxiId), new TaxiUnit
            {
                TaxiId = taxiId,
                PlateNumber = plate,
                Model = InputValidator.NormalizeSpaces(input.Model),
                YearManufactured = input.YearManufactured!.Value,
                CurrentMileage = input.Mileage!.Value,
                Status = TaxiStatusRules.ActiveUnit,
            });
            Audit(transaction, actorUserId, "TaxiUnitAdded", $"Added taxi unit {taxiId} (plate {plate}).");
            return FleetActionResult.Success(taxiId);
        });
    }

    public async Task<FleetActionResult> UpdateUnitAsync(string taxiId, FleetUnitInput input, string actorUserId)
    {
        if (Validate(input) is string error)
        {
            return FleetActionResult.Fail(error);
        }

        return await RunAsync($"update {taxiId}", actorUserId, async transaction =>
        {
            DocumentSnapshot current = await transaction.GetSnapshotAsync(Taxis.Document(taxiId));
            if (!current.Exists)
            {
                return FleetActionResult.Fail("This unit no longer exists.");
            }

            TaxiUnit taxi = current.ConvertTo<TaxiUnit>();
            QuerySnapshot all = await transaction.GetSnapshotAsync(Taxis);
            string plate = FleetUnitRules.NormalizePlate(input.PlateNumber);
            if (FleetUnitRules.PlateKey(plate) != FleetUnitRules.PlateKey(taxi.PlateNumber)
                && PlateOwner(all, plate, taxiId) is string owner)
            {
                return FleetActionResult.Fail(owner);
            }

            var updates = new Dictionary<string, object>();
            var changes = new List<string>();
            void Change(string field, string label, object oldValue, object newValue)
            {
                if (!Equals(oldValue, newValue))
                {
                    updates[field] = newValue;
                    changes.Add($"{label} {oldValue} to {newValue}");
                }
            }

            Change("plateNumber", "plate", taxi.PlateNumber, plate);
            Change("model", "model", taxi.Model, InputValidator.NormalizeSpaces(input.Model));
            Change("yearManufactured", "year", taxi.YearManufactured, input.YearManufactured!.Value);

            int mileage = input.Mileage!.Value;
            if (mileage != taxi.CurrentMileage)
            {
                QuerySnapshot shifts = await transaction.GetSnapshotAsync(Db.Collection("shifts").WhereEqualTo("taxiId", taxiId));
                if (LatestOdometerByUnit(shifts.Documents).TryGetValue(taxiId, out int reading) && mileage < reading)
                {
                    return FleetActionResult.Fail($"Mileage can't be less than {reading:N0} km, the latest odometer reading from this unit's shifts.");
                }
                Change("currentMileage", "mileage", taxi.CurrentMileage, mileage);
            }

            if (input.Status is string status && !string.Equals(status, taxi.Status, StringComparison.OrdinalIgnoreCase))
            {
                if (TaxiStatusRules.IsRetired(taxi.Status))
                {
                    return FleetActionResult.Fail("This unit is retired - use Restore to put it back in service.");
                }
                if (status != TaxiStatusRules.ActiveUnit && status != TaxiStatusRules.UnderMaintenance)
                {
                    return FleetActionResult.Fail("Choose Active Unit or Under Maintenance. To take a unit out of service, use Delete.");
                }
                if (status == TaxiStatusRules.ActiveUnit)
                {
                    QuerySnapshot jobs = await transaction.GetSnapshotAsync(Db.Collection("maintenance_logs").WhereEqualTo("taxiId", taxiId));
                    if (jobs.Documents.Any(j => string.Equals(Text(j, "status"), WorkOrderRules.InProgress, StringComparison.OrdinalIgnoreCase)))
                    {
                        return FleetActionResult.Fail("This unit has a work order in the shop. Mark it resolved in the Garage and the unit returns to service.");
                    }
                }
                Change("status", "status", taxi.Status, status);
            }

            if (updates.Count > 0)
            {
                transaction.Update(current.Reference, updates);
                Audit(transaction, actorUserId, "TaxiUnitUpdated", $"Updated taxi unit {taxiId}: {string.Join("; ", changes)}.");
            }
            return FleetActionResult.Success(taxiId);
        });
    }

    /// <summary>What <see cref="DeleteUnitAsync"/> would do right now. It checks everything again.</summary>
    public Task<UnitRemovalPlan> GetRemovalPlanAsync(string taxiId) =>
        Db.RunTransactionAsync(async transaction => (await ReadRemovalAsync(transaction, taxiId)).Plan);

    /// <summary>Removes a unit nothing refers to; retires (Decommissioned) one with shifts, Garage jobs
    /// or other records so those stay intact. Refused while anything current depends on it.</summary>
    public Task<FleetActionResult> DeleteUnitAsync(string taxiId, string actorUserId) =>
        RunAsync($"delete {taxiId}", actorUserId, async transaction =>
        {
            DocumentSnapshot current = await transaction.GetSnapshotAsync(Taxis.Document(taxiId));
            if (!current.Exists)
            {
                return FleetActionResult.Fail("This unit no longer exists.");
            }

            UnitRemoval removal = await ReadRemovalAsync(transaction, taxiId);
            if (removal.Plan.Blockers.Count > 0)
            {
                return FleetActionResult.Fail(string.Join(" ", removal.Plan.Blockers));
            }

            string plate = Text(current, "plateNumber");
            if (removal.Plan.HasHistory)
            {
                if (TaxiStatusRules.IsRetired(Text(current, "status")))
                {
                    return FleetActionResult.Fail("This unit is already retired. Its records keep it from being removed completely.");
                }

                transaction.Update(current.Reference, "status", TaxiStatusRules.Decommissioned);
                CancelApprovedClockIns(transaction, removal.ApprovedClockIns);
                Audit(transaction, actorUserId, "TaxiUnitRetired", $"Retired taxi unit {taxiId} (plate {plate}); its records are kept.");
                return FleetActionResult.Success(taxiId, retired: true);
            }

            // Nothing happened on this unit: drop it with the routine checks and alerts made for it.
            QuerySnapshot checks = await transaction.GetSnapshotAsync(Db.Collection("routine_checks").WhereEqualTo("taxiId", taxiId));
            QuerySnapshot alerts = await transaction.GetSnapshotAsync(Db.Collection("system_alerts").WhereEqualTo("taxiId", taxiId));
            foreach (DocumentSnapshot doc in checks.Documents.Concat(alerts.Documents))
            {
                transaction.Delete(doc.Reference);
            }
            transaction.Delete(current.Reference);
            transaction.Set(Db.Collection(DeletedIdsCollection).Document(taxiId), new Dictionary<string, object>
            {
                ["plateNumber"] = plate,
                ["deletedAt"] = DateTime.UtcNow,
                ["deletedBy"] = actorUserId,
            });
            Audit(transaction, actorUserId, "TaxiUnitDeleted", $"Deleted taxi unit {taxiId} (plate {plate}); it had no records.");
            return FleetActionResult.Success(taxiId);
        });

    public Task<FleetActionResult> RestoreUnitAsync(string taxiId, string actorUserId) =>
        RunAsync($"restore {taxiId}", actorUserId, async transaction =>
        {
            DocumentSnapshot current = await transaction.GetSnapshotAsync(Taxis.Document(taxiId));
            if (!current.Exists || !TaxiStatusRules.IsRetired(Text(current, "status")))
            {
                return FleetActionResult.Fail("This unit isn't retired.");
            }

            transaction.Update(current.Reference, "status", TaxiStatusRules.ActiveUnit);
            Audit(transaction, actorUserId, "TaxiUnitRestored", $"Restored taxi unit {taxiId} (plate {Text(current, "plateNumber")}) to service.");
            return FleetActionResult.Success(taxiId);
        });

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private sealed record UnitRemoval(UnitRemovalPlan Plan, List<DocumentReference> ApprovedClockIns);

    private async Task<UnitRemoval> ReadRemovalAsync(Transaction transaction, string taxiId)
    {
        Task<QuerySnapshot> Where(string collection, string field) =>
            transaction.GetSnapshotAsync(Db.Collection(collection).WhereEqualTo(field, taxiId));

        QuerySnapshot holders = await Where("users", "assignedTaxiId");
        QuerySnapshot shifts = await Where("shifts", "taxiId");
        QuerySnapshot jobs = await Where("maintenance_logs", "taxiId");
        QuerySnapshot clockIns = await Where("clockin_requests", "taxiId");
        QuerySnapshot bookings = await Where("shift_schedules", "taxiId");
        QuerySnapshot sos = await Where("emergency_alerts", "taxiId");

        DateTime today = PhilippineTime.Now.Date;
        // Schedule days are stored as the calendar date at midnight UTC.
        List<(DateTime Day, bool IsSubstitute)> days = bookings.Documents
            .Where(b => b.TryGetValue("scheduledStartTime", out Timestamp _))
            .Select(b => (b.GetValue<Timestamp>("scheduledStartTime").ToDateTime().Date, Text(b, "status") == "Substitute"))
            .ToList();
        List<DateTime> upcoming = days.Where(d => d.IsSubstitute && d.Day >= today).Select(d => d.Day).ToList();

        var facts = new UnitRemovalFacts(
            holders.Documents.Where(u => StaffRoles.IsDriver(Text(u, "role"))).Select(u => Text(u, "fullName")).ToList(),
            shifts.Documents.Any(s => Text(s, "status") == "Active"),
            jobs.Documents.Count(j => TaxiStatusRules.IsOpenJob(Text(j, "status"))
                                      || string.Equals(Text(j, "status"), WorkOrderRules.Scheduled, StringComparison.OrdinalIgnoreCase)),
            clockIns.Documents.Any(c => Text(c, "status") == ClockInRequest.Pending),
            upcoming);

        bool hasHistory = shifts.Count > 0 || jobs.Count > 0 || clockIns.Count > 0 || sos.Count > 0 || days.Any(d => d.Day < today);

        return new UnitRemoval(
            new UnitRemovalPlan { Blockers = FleetUnitRules.RemovalBlockers(facts), HasHistory = hasHistory },
            clockIns.Documents.Where(c => Text(c, "status") == ClockInRequest.Approved).Select(c => c.Reference).ToList());
    }

    // An approval the driver's phone never used would otherwise still start a shift on the retired unit.
    private static void CancelApprovedClockIns(Transaction transaction, List<DocumentReference> requests)
    {
        foreach (DocumentReference request in requests)
        {
            transaction.Update(request, "status", ClockInRequest.Cancelled);
        }
    }

    private async Task<FleetActionResult> RunAsync(string what, string actorUserId, Func<Transaction, Task<FleetActionResult>> work)
    {
        try
        {
            if (!await ManagerCheck.IsManagerAsync(Db, actorUserId))
            {
                return FleetActionResult.Fail(ManagerOnlyMessage);
            }

            return await Db.RunTransactionAsync(work);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to {Action}", what);
            return FleetActionResult.Fail("That couldn't be saved. Please try again.");
        }
    }

    private static string? Validate(FleetUnitInput input) =>
        FleetUnitRules.ValidatePlate(input.PlateNumber)
        ?? FleetUnitRules.ValidateModel(input.Model)
        ?? FleetUnitRules.ValidateYear(input.YearManufactured, PhilippineTime.Now)
        ?? FleetUnitRules.ValidateMileage(input.Mileage);

    private static string? PlateOwner(QuerySnapshot taxis, string plate, string? exceptTaxiId)
    {
        string key = FleetUnitRules.PlateKey(plate);
        DocumentSnapshot? other = taxis.Documents.FirstOrDefault(d =>
            d.Id != exceptTaxiId && FleetUnitRules.PlateKey(Text(d, "plateNumber")) == key);
        if (other is null)
        {
            return null;
        }

        return TaxiStatusRules.IsRetired(Text(other, "status"))
            ? $"Plate {plate} belongs to {other.Id}, which is retired. Restore that unit instead."
            : $"Plate {plate} is already used by {other.Id}.";
    }

    // The latest shift's end reading, or its start reading while it's still running.
    private static Dictionary<string, int> LatestOdometerByUnit(IEnumerable<DocumentSnapshot> shifts)
    {
        var latest = new Dictionary<string, (DateTime Start, int Reading)>(StringComparer.OrdinalIgnoreCase);
        foreach (DocumentSnapshot shift in shifts)
        {
            string taxiId = Text(shift, "taxiId");
            int reading = Number(shift, "endMileage") is int end && end > 0 ? end : Number(shift, "startMileage");
            DateTime start = shift.TryGetValue("shiftStart", out Timestamp? at) && at is Timestamp t ? t.ToDateTime() : DateTime.MinValue;
            if (taxiId.Length > 0 && reading > 0 && (!latest.TryGetValue(taxiId, out var seen) || start > seen.Start))
            {
                latest[taxiId] = (start, reading);
            }
        }

        return latest.ToDictionary(p => p.Key, p => p.Value.Reading, StringComparer.OrdinalIgnoreCase);
    }

    private static string Text(DocumentSnapshot doc, string field) =>
        doc.TryGetValue(field, out object? value) && value is string text ? text : string.Empty;

    private static int Number(DocumentSnapshot doc, string field) =>
        doc.TryGetValue(field, out object? value) ? value switch { long l => (int)l, double d => (int)d, _ => 0 } : 0;

    private void Audit(Transaction transaction, string actorUserId, string actionType, string details) =>
        transaction.Create(Db.Collection("audit_logs").Document(), new AuditLog
        {
            UserId = actorUserId,
            ActionType = actionType,
            AuditLogDetails = details,
            Timestamp = DateTime.UtcNow,
        });
}
