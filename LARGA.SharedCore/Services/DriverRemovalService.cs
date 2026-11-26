using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FirebaseAdmin.Auth;
using Google.Cloud.Firestore;
using LARGA.Shared.Models.Entities;
using LARGA.SharedCore.Models.Fleet;
using LARGA.SharedCore.Push;
using Microsoft.Extensions.Logging;

namespace LARGA.SharedCore.Services;

/// <summary>
/// ManagerWeb's "Delete Driver". The driver's login is deleted and they lose the Driver role, so they
/// leave the roster, planner and pickers; their profile keeps its name, and their shifts, payments and
/// fuel reports stay as they were. Manager-only, and refused while anything current depends on them
/// (<see cref="DriverRemovalRules"/>).
/// </summary>
public class DriverRemovalService
{
    private const string ManagerOnlyMessage = "Only the Manager can delete a driver.";
    private const string NotADriverMessage = "This driver has already been deleted.";

    private readonly Lazy<FirestoreDb> _dbLazy;
    private readonly Lazy<FirebaseAuth> _authLazy;
    private readonly FinancialLedgerService _ledger;
    private readonly ILogger<DriverRemovalService> _logger;

    private FirestoreDb Db => _dbLazy.Value;

    public DriverRemovalService(
        Lazy<FirestoreDb> dbLazy,
        Lazy<FirebaseAuth> authLazy,
        FinancialLedgerService ledger,
        ILogger<DriverRemovalService> logger)
    {
        _dbLazy = dbLazy;
        _authLazy = authLazy;
        _ledger = ledger;
        _logger = logger;
    }

    /// <summary>What <see cref="DeleteAsync"/> would run into right now (null: not a driver any more).
    /// Delete checks everything again.</summary>
    public async Task<DriverRemovalPlan?> GetPlanAsync(string driverId)
    {
        (decimal owed, decimal credit) = await _ledger.GetDriverBalanceAsync(driverId);
        return await Db.RunTransactionAsync(async transaction =>
        {
            DriverState? state = await ReadAsync(transaction, driverId);
            return state is null ? null : new DriverRemovalPlan
            {
                FullName = state.FullName,
                AssignedTaxiId = state.AssignedTaxiId,
                AdvanceCredit = credit,
                Blockers = state.Blockers(owed),
            };
        });
    }

    public async Task<DriverRemovalResult> DeleteAsync(string driverId, string actorUserId)
    {
        string fullName;
        try
        {
            if (!await ManagerCheck.IsManagerAsync(Db, actorUserId))
            {
                return DriverRemovalResult.Fail(ManagerOnlyMessage);
            }

            (decimal owed, decimal credit) = await _ledger.GetDriverBalanceAsync(driverId);
            DateTime now = DateTime.UtcNow;

            (string? error, string name) = await Db.RunTransactionAsync(async transaction =>
            {
                DriverState? state = await ReadAsync(transaction, driverId);
                if (state is null)
                {
                    return ((string?)NotADriverMessage, string.Empty);
                }

                List<string> blockers = state.Blockers(owed);
                if (blockers.Count > 0)
                {
                    return (string.Join(" ", blockers), state.FullName);
                }

                transaction.Update(state.Profile, new Dictionary<string, object>
                {
                    ["role"] = StaffRoles.FormerDriver,
                    ["assignedTaxiId"] = string.Empty,
                    ["debtFlaggedSince"] = FieldValue.Delete,
                    [ForcedPasswordChangeRules.Field] = false,
                    ["deletedAt"] = now,
                    ["deletedBy"] = actorUserId,
                });
                foreach (DocumentReference day in state.UpcomingScheduleDays)
                {
                    transaction.Delete(day);
                }
                foreach (DocumentReference request in state.ApprovedClockIns)
                {
                    transaction.Update(request, "status", ClockInRequest.Cancelled);
                }
                transaction.Create(Db.Collection("audit_logs").Document(), new AuditLog
                {
                    UserId = actorUserId,
                    ActionType = "DriverDeleted",
                    AuditLogDetails = $"Deleted driver '{state.FullName}' (driver ID: {driverId})"
                        + (state.AssignedTaxiId is null ? "" : $", freeing unit {state.AssignedTaxiId}")
                        + (credit > 0 ? $", with ₱{credit:N2} unused advance credit" : "")
                        + "; shift and payment records are kept.",
                    Timestamp = now,
                });
                return ((string?)null, state.FullName);
            });

            if (error is not null)
            {
                return DriverRemovalResult.Fail(error);
            }
            fullName = name;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete driver {DriverId}", driverId);
            return DriverRemovalResult.Fail("The driver couldn't be deleted. Please try again.");
        }

        // The profile change above already shuts them out (no role). These two only tidy up.
        var warnings = new List<string>();
        try
        {
            await _authLazy.Value.DeleteUserAsync(driverId);
        }
        catch (FirebaseAuthException ex) when (ex.AuthErrorCode == AuthErrorCode.UserNotFound)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete the login of deleted driver {DriverId}", driverId);
            warnings.Add($"{fullName} was removed and can't use the app, but their login couldn't be deleted, so their email can't be reused yet. Delete it in the Firebase console.");
        }

        try
        {
            QuerySnapshot tokens = await Db.Collection("users").Document(driverId).Collection(PushTokenRules.Collection).GetSnapshotAsync();
            foreach (DocumentSnapshot token in tokens.Documents)
            {
                await token.Reference.DeleteAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete the push tokens of deleted driver {DriverId}", driverId);
        }

        return new DriverRemovalResult { Ok = true, Warning = warnings.Count == 0 ? null : string.Join(" ", warnings) };
    }

    private sealed record DriverState(
        DocumentReference Profile,
        string FullName,
        string? AssignedTaxiId,
        bool IsOnShift,
        bool HasPendingClockIn,
        int UnresolvedSos,
        List<DocumentReference> UpcomingScheduleDays,
        List<DocumentReference> ApprovedClockIns)
    {
        public List<string> Blockers(decimal owed) =>
            DriverRemovalRules.Blockers(IsOnShift, HasPendingClockIn, UnresolvedSos, owed);
    }

    private async Task<DriverState?> ReadAsync(Transaction transaction, string driverId)
    {
        DocumentReference profileRef = Db.Collection("users").Document(driverId);
        DocumentSnapshot profile = await transaction.GetSnapshotAsync(profileRef);
        if (!profile.Exists || !StaffRoles.IsDriver(Text(profile, "role")))
        {
            return null;
        }

        Task<QuerySnapshot> Mine(string collection) =>
            transaction.GetSnapshotAsync(Db.Collection(collection).WhereEqualTo("driverId", driverId));

        QuerySnapshot shifts = await Mine("shifts");
        QuerySnapshot clockIns = await Mine("clockin_requests");
        QuerySnapshot schedule = await Mine("shift_schedules");
        // Older SOS alerts carry no driverId, only the shift they came from.
        QuerySnapshot openSos = await transaction.GetSnapshotAsync(Db.Collection("emergency_alerts").WhereEqualTo("isResolved", false));
        HashSet<string> shiftIds = shifts.Documents.Select(s => s.Id).ToHashSet();

        DateTime today = PhilippineTime.Now.Date;
        string assigned = Text(profile, "assignedTaxiId");
        string fullName = Text(profile, "fullName");

        return new DriverState(
            profileRef,
            fullName.Length == 0 ? driverId : fullName,
            assigned.Length == 0 ? null : assigned,
            shifts.Documents.Any(s => Text(s, "status") == "Active"),
            clockIns.Documents.Any(c => Text(c, "status") == ClockInRequest.Pending),
            openSos.Documents.Count(a => Text(a, "driverId") == driverId || shiftIds.Contains(Text(a, "shiftId"))),
            schedule.Documents
                .Where(d => d.TryGetValue("scheduledStartTime", out Timestamp _)
                            && d.GetValue<Timestamp>("scheduledStartTime").ToDateTime().Date >= today)
                .Select(d => d.Reference)
                .ToList(),
            clockIns.Documents.Where(c => Text(c, "status") == ClockInRequest.Approved).Select(c => c.Reference).ToList());
    }

    private static string Text(DocumentSnapshot doc, string field) =>
        doc.TryGetValue(field, out object? value) && value is string text ? text : string.Empty;
}
