using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using FirebaseAdmin.Auth;
using Google.Cloud.Firestore;
using LARGA.SharedCore.Models.DriverShifts;
using LARGA.Shared.Models.Entities;
using Microsoft.Extensions.Logging;

namespace LARGA.SharedCore.Services;

/// <summary>
/// Backs the ManagerWeb Driver &amp; Shift Management page: the live roster, the weekly
/// schedule planner, shift logs + handover checklists, and driver account management
/// (managers create driver accounts themselves - drivers never self-register, per the
/// team's auth model).
///
/// Two separate lazy Firebase clients, same pattern as FleetReportingService and for the
/// same reason: a missing-credentials failure should surface inside a page's own
/// try/catch, not crash at DI-construction time.
/// </summary>
public class DriverManagementService
{
    private readonly Lazy<FirestoreDb> _dbLazy;
    private readonly Lazy<FirebaseAuth> _authLazy;
    private readonly ILogger<DriverManagementService> _logger;

    private FirestoreDb Db => _dbLazy.Value;
    private FirebaseAuth Auth => _authLazy.Value;

    public DriverManagementService(Lazy<FirestoreDb> dbLazy, Lazy<FirebaseAuth> authLazy, ILogger<DriverManagementService> logger)
    {
        _dbLazy = dbLazy;
        _authLazy = authLazy;
        _logger = logger;
    }

    // ---------------------------------------------------------------------
    // Live Roster
    // ---------------------------------------------------------------------

    public async Task<RosterSnapshot> GetRosterSnapshotAsync()
    {
        DateTime now = DateTime.UtcNow;
        List<UserProfile> drivers = await GetDriversAsync();
        List<ShiftLog> activeShifts = await GetWhereEqualAsync<ShiftLog>("shifts", "status", "Active");

        var entries = drivers.Select(d =>
        {
            ShiftLog? active = activeShifts.FirstOrDefault(s => s.DriverId == d.UserId);
            return new DriverRosterEntry
            {
                DriverId = d.UserId,
                FullName = d.FullName,
                LicenseStatus = ComputeLicenseStatus(d.LicenseExpiryDate, now),
                IsOnShift = active is not null,
                AssignedTaxiId = active?.TaxiId ?? (string.IsNullOrWhiteSpace(d.AssignedTaxiId) ? null : d.AssignedTaxiId),
            };
        }).ToList();

        return new RosterSnapshot
        {
            TotalDrivers = entries.Count,
            OnShiftCount = entries.Count(e => e.IsOnShift),
            OffDutyCount = entries.Count(e => !e.IsOnShift),
            ExpiredLicenseCount = entries.Count(e => e.LicenseStatus == LicenseStatus.Expired),
            Drivers = entries,
        };
    }

    private static LicenseStatus ComputeLicenseStatus(DateTime? expiry, DateTime now)
    {
        if (expiry is null)
        {
            return LicenseStatus.NotSet;
        }

        if (expiry.Value < now)
        {
            return LicenseStatus.Expired;
        }

        // 30 days is a judgment call - long enough for a manager to notice and remind the
        // driver to renew before it actually lapses.
        return expiry.Value <= now.AddDays(30) ? LicenseStatus.Expiring : LicenseStatus.Valid;
    }

    // ---------------------------------------------------------------------
    // Schedule Planner
    // ---------------------------------------------------------------------

    public async Task<WeekSchedule> GetWeekScheduleAsync(DateTime weekStartUtc)
    {
        DateTime weekStart = StartOfWeek(weekStartUtc);
        DateTime weekEnd = weekStart.AddDays(7);

        List<UserProfile> drivers = await GetDriversAsync();
        List<TaxiUnit> taxis = await GetAllAsync<TaxiUnit>("taxis");
        List<ShiftSchedule> schedules = await GetBetweenAsync<ShiftSchedule>("shift_schedules", "scheduledStartTime", weekStart, weekEnd.AddTicks(-1));

        var rows = new List<DriverScheduleRow>();
        var unitsByDay = new int[7];

        foreach (UserProfile driver in drivers)
        {
            var row = new DriverScheduleRow
            {
                DriverId = driver.UserId,
                FullName = driver.FullName,
                LicenseStatus = ComputeLicenseStatus(driver.LicenseExpiryDate, DateTime.UtcNow),
            };

            for (int i = 0; i < 7; i++)
            {
                DateTime day = weekStart.AddDays(i);
                ShiftSchedule? match = schedules.FirstOrDefault(s => s.DriverId == driver.UserId && s.ScheduledStartTime.Date == day.Date);
                string? taxiId = string.IsNullOrWhiteSpace(match?.TaxiId) ? null : match!.TaxiId;

                row.Days.Add(new ScheduleDayCell { Date = day, TaxiId = taxiId });

                if (taxiId is not null)
                {
                    unitsByDay[i]++;
                }
            }

            rows.Add(row);
        }

        return new WeekSchedule
        {
            WeekStart = weekStart,
            Rows = rows,
            UnitsActiveByDay = unitsByDay.ToList(),
            TotalTaxis = taxis.Count,
            TaxiIds = taxis.Select(t => t.TaxiId).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }

    private static DateTime StartOfWeek(DateTime date)
    {
        DateTime d = date.Date;
        int diff = (7 + (int)d.DayOfWeek - (int)DayOfWeek.Monday) % 7;
        return DateTime.SpecifyKind(d.AddDays(-diff), DateTimeKind.Utc);
    }

    private static string ScheduleDocId(string driverId, DateTime date) => $"{driverId}_{date:yyyyMMdd}";

    public async Task AssignScheduleAsync(string driverId, DateTime dateUtc, string taxiId)
    {
        var schedule = new ShiftSchedule
        {
            DriverId = driverId,
            TaxiId = taxiId,
            ScheduledStartTime = DateTime.SpecifyKind(dateUtc.Date, DateTimeKind.Utc),
            Status = "Planned",
        };
        await Db.Collection("shift_schedules").Document(ScheduleDocId(driverId, dateUtc)).SetAsync(schedule, SetOptions.Overwrite);
    }

    public async Task ClearScheduleAsync(string driverId, DateTime dateUtc)
    {
        await Db.Collection("shift_schedules").Document(ScheduleDocId(driverId, dateUtc)).DeleteAsync();
    }

    // ---------------------------------------------------------------------
    // Shift Logs
    // ---------------------------------------------------------------------

    public async Task<List<ShiftLogEntry>> GetShiftLogsAsync(DateTime fromUtc, DateTime toUtc)
    {
        List<ShiftLog> shifts = await GetBetweenAsync<ShiftLog>("shifts", "shiftStart", fromUtc, toUtc);
        List<UserProfile> drivers = await GetDriversAsync();
        List<HandoverChecklist> checklists = await GetBetweenAsync<HandoverChecklist>("handover_checklists", "timestamp", fromUtc, toUtc.AddDays(1));

        return shifts
            .OrderByDescending(s => s.ShiftStart)
            .Select(s =>
            {
                List<HandoverChecklist> shiftChecklists = checklists.Where(c => c.ShiftId == s.ShiftId).ToList();
                int defectCount = shiftChecklists.Sum(c => EvaluateChecklist(c).Items.Count(i => i.Passed == false));

                return new ShiftLogEntry
                {
                    ShiftId = s.ShiftId,
                    ShiftStart = s.ShiftStart,
                    DriverId = s.DriverId,
                    DriverName = drivers.FirstOrDefault(d => d.UserId == s.DriverId)?.FullName ?? s.DriverId,
                    TaxiId = s.TaxiId,
                    Status = s.Status,
                    HasPreShiftChecklist = shiftChecklists.Any(c => c.ChecklistType == ChecklistType.PreShift),
                    HasEndShiftChecklist = shiftChecklists.Any(c => c.ChecklistType == ChecklistType.EndShift),
                    DefectCount = defectCount,
                };
            })
            .ToList();
    }

    public async Task<ShiftChecklists?> GetShiftChecklistsAsync(string shiftId)
    {
        DocumentSnapshot shiftDoc = await Db.Collection("shifts").Document(shiftId).GetSnapshotAsync();
        if (!shiftDoc.Exists)
        {
            return null;
        }

        ShiftLog shift = shiftDoc.ConvertTo<ShiftLog>();
        List<UserProfile> drivers = await GetDriversAsync();
        List<HandoverChecklist> checklists = await GetWhereEqualAsync<HandoverChecklist>("handover_checklists", "shiftId", shiftId);

        return new ShiftChecklists
        {
            ShiftId = shiftId,
            TaxiId = shift.TaxiId,
            DriverName = drivers.FirstOrDefault(d => d.UserId == shift.DriverId)?.FullName ?? shift.DriverId,
            PreShift = checklists.Where(c => c.ChecklistType == ChecklistType.PreShift).Select(EvaluateChecklist).FirstOrDefault(),
            EndShift = checklists.Where(c => c.ChecklistType == ChecklistType.EndShift).Select(EvaluateChecklist).FirstOrDefault(),
        };
    }

    // Firestore stores 5 raw signals (tireCondition, oilLevel, coolantLevel,
    // interiorCleanliness, exteriorScratches, fuelVerification) - oil+coolant are combined
    // into one "under the hood" row here since the mobile checklist presents them as a
    // single inspection step. There's no field anywhere for "starting odometer documented"
    // (that reading lives on FuelLog, not HandoverChecklist), so that row is intentionally
    // omitted rather than fabricated.
    private static ChecklistDetail EvaluateChecklist(HandoverChecklist c)
    {
        var items = new List<ChecklistItemResult>
        {
            new() { Label = "Tire Condition", Passed = c.TireCondition },
            new() { Label = "Checked under the hood (Oil Level & Coolant/Water OK)", Passed = c.OilLevel && c.CoolantLevel },
            new() { Label = "Interior Cleanliness & Comfort", Passed = c.InteriorCleanliness },
            new() { Label = "Checked exterior Scratches / Dents", Passed = c.ExteriorScratches },
            new()
            {
                Label = "Fuel Verification",
                Passed = null,
                ValueText = c.FuelVerification == FuelVerification.BelowHalfTank ? "Below half-tank" : "Half-tank",
            },
        };

        return new ChecklistDetail
        {
            ChecklistType = c.ChecklistType == ChecklistType.EndShift ? "Post-Shift" : "Pre-Shift",
            Timestamp = c.Timestamp,
            Items = items,
            PrimaryPhotoUrl = c.ScratchesPhotoUrl,
            PrimaryPhotoLabel = "Exterior / Scratches Photo",
            SecondaryPhotoUrl = c.FuelDashboardUrl,
            SecondaryPhotoLabel = "Fuel Dashboard Photo",
        };
    }

    // ---------------------------------------------------------------------
    // Driver Profile
    // ---------------------------------------------------------------------

    public async Task<DriverProfileDetail?> GetDriverProfileAsync(string driverId)
    {
        DocumentSnapshot doc = await Db.Collection("users").Document(driverId).GetSnapshotAsync();
        if (!doc.Exists)
        {
            return null;
        }

        UserProfile profile = doc.ConvertTo<UserProfile>();

        // Single-driver, on-demand lookup (only runs when a manager opens this one profile)
        // rather than something that runs on every page load - full history for just this
        // driver is an acceptable, deliberate exception to the fleet-wide windowing used
        // elsewhere (see FleetReportingService's read-cost notes).
        List<ShiftLog> driverShifts = await GetWhereEqualAsync<ShiftLog>("shifts", "driverId", driverId);
        HashSet<string> shiftIds = driverShifts.Select(s => s.ShiftId).ToHashSet();

        List<BoundaryPayment> allPayments = await GetAllAsync<BoundaryPayment>("boundary_payments");
        List<MaintenanceRecord> allMaintenance = await GetAllAsync<MaintenanceRecord>("maintenance_logs");

        List<BoundaryPayment> driverPayments = allPayments.Where(p => shiftIds.Contains(p.ShiftId)).ToList();
        int damageCount = allMaintenance.Count(m => m.MaintenanceType == MaintenanceType.AccidentCorrection && m.ShiftId is not null && shiftIds.Contains(m.ShiftId));

        double punctualPercent = driverShifts.Count == 0
            ? 0
            : 100.0 * driverShifts.Count(s => s.Status != "Overdue") / driverShifts.Count;

        double paymentReliabilityPercent = driverPayments.Count == 0
            ? 0
            : 100.0 * driverPayments.Count(p => p.PaymentStatus == PaymentStatus.Paid) / driverPayments.Count;

        ShiftLog? activeShift = driverShifts.FirstOrDefault(s => s.Status == "Active");
        DateTime now = DateTime.UtcNow;

        return new DriverProfileDetail
        {
            DriverId = profile.UserId,
            FullName = profile.FullName,
            PhoneNumber = profile.PhoneNumber,
            Address = profile.Address,
            DateJoined = profile.DateJoined,
            IsOnShift = activeShift is not null,
            AssignedTaxiId = activeShift?.TaxiId ?? (string.IsNullOrWhiteSpace(profile.AssignedTaxiId) ? null : profile.AssignedTaxiId),
            LicenseNumber = profile.LicenseNumber,
            LicenseClassification = profile.LicenseClassification,
            LicenseRestrictionCode = profile.LicenseRestrictionCode,
            LicenseStatus = ComputeLicenseStatus(profile.LicenseExpiryDate, now),
            LicenseExpiryDate = profile.LicenseExpiryDate,
            LtoIdPhotoUrl = profile.LtoIdPhotoUrl,
            PunctualPercent = punctualPercent,
            PaymentReliabilityPercent = paymentReliabilityPercent,
            DamageIncidentCount = damageCount,
            ManagerNote = profile.ManagerNote,
        };
    }

    public async Task UpdateDriverProfileAsync(
        string driverId,
        string? phoneNumber,
        string? address,
        string? licenseNumber,
        string? licenseClassification,
        string? licenseRestrictionCode,
        DateTime? licenseExpiryDate,
        string? assignedTaxiId)
    {
        var updates = new Dictionary<string, object>
        {
            ["phoneNumber"] = phoneNumber ?? string.Empty,
            ["address"] = address ?? string.Empty,
            ["licenseNumber"] = licenseNumber ?? string.Empty,
            ["licenseClassification"] = licenseClassification ?? string.Empty,
            ["licenseRestrictionCode"] = licenseRestrictionCode ?? string.Empty,
            ["assignedTaxiId"] = assignedTaxiId ?? string.Empty,
        };

        if (licenseExpiryDate.HasValue)
        {
            updates["licenseExpiryDate"] = licenseExpiryDate.Value;
        }

        await Db.Collection("users").Document(driverId).UpdateAsync(updates);
    }

    public async Task SetManagerNoteAsync(string driverId, string note)
    {
        await Db.Collection("users").Document(driverId).UpdateAsync("managerNote", note);
    }

    // ---------------------------------------------------------------------
    // Driver account management (manager-provisioned - drivers never self-register)
    // ---------------------------------------------------------------------

    public async Task<CreateDriverResult> CreateDriverAsync(string fullName, string phoneNumber, string? temporaryPassword)
    {
        string password = string.IsNullOrWhiteSpace(temporaryPassword) ? GenerateTemporaryPassword() : temporaryPassword;
        if (password.Length < 6)
        {
            return new CreateDriverResult { Ok = false, ErrorMessage = "Temporary password must be at least 6 characters." };
        }

        // Drivers sign in with email+password (same as the mobile app's login screen), but
        // the "Add New Driver" form intentionally only collects name/phone/password - there
        // is no self-registration for drivers, so the login email itself doesn't need to
        // mean anything to them. The manager relays whatever this generates, along with the
        // password, to the driver directly.
        string email = GenerateDriverEmail(fullName);

        UserRecord userRecord;
        try
        {
            userRecord = await Auth.CreateUserAsync(new UserRecordArgs
            {
                Email = email,
                Password = password,
                DisplayName = fullName,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create Firebase Auth account for new driver {FullName}", fullName);
            return new CreateDriverResult { Ok = false, ErrorMessage = ex.Message };
        }

        var profile = new UserProfile
        {
            FullName = fullName,
            Email = email,
            PhoneNumber = phoneNumber,
            Role = "Driver",
            DateJoined = DateTime.UtcNow,
            MustChangePassword = true,
        };

        try
        {
            await Db.Collection("users").Document(userRecord.Uid).SetAsync(profile, SetOptions.Overwrite);
        }
        catch (Exception ex)
        {
            // The Auth account exists but the Firestore profile write failed - clean up so
            // we don't leave an orphaned login with no profile behind.
            _logger.LogWarning(ex, "Firestore profile write failed for new driver {Uid}; rolling back the Auth account", userRecord.Uid);
            try
            {
                await Auth.DeleteUserAsync(userRecord.Uid);
            }
            catch (Exception cleanupEx)
            {
                _logger.LogWarning(cleanupEx, "Also failed to roll back the orphaned Auth account {Uid}", userRecord.Uid);
            }

            return new CreateDriverResult { Ok = false, ErrorMessage = ex.Message };
        }

        return new CreateDriverResult
        {
            Ok = true,
            DriverId = userRecord.Uid,
            GeneratedEmail = email,
            TemporaryPassword = password,
        };
    }

    public async Task<ResetPasswordResult> ResetDriverPasswordAsync(string driverId, string? newPassword)
    {
        string password = string.IsNullOrWhiteSpace(newPassword) ? GenerateTemporaryPassword() : newPassword;
        if (password.Length < 6)
        {
            return new ResetPasswordResult { Ok = false, ErrorMessage = "Password must be at least 6 characters." };
        }

        try
        {
            await Auth.UpdateUserAsync(new UserRecordArgs { Uid = driverId, Password = password });
            await Db.Collection("users").Document(driverId).UpdateAsync("mustChangePassword", true);
            return new ResetPasswordResult { Ok = true, NewPassword = password };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to reset password for driver {DriverId}", driverId);
            return new ResetPasswordResult { Ok = false, ErrorMessage = ex.Message };
        }
    }

    private static string GenerateDriverEmail(string fullName)
    {
        string slug = new string(fullName.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '.').ToArray());
        while (slug.Contains("..")) slug = slug.Replace("..", ".");
        slug = slug.Trim('.');
        if (string.IsNullOrWhiteSpace(slug))
        {
            slug = "driver";
        }

        string suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        return $"{slug}.{suffix}@larga-driver.local";
    }

    private static string GenerateTemporaryPassword()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        var bytes = RandomNumberGenerator.GetBytes(12);
        var sb = new StringBuilder(12);
        foreach (byte b in bytes)
        {
            sb.Append(chars[b % chars.Length]);
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------------
    // Helpers (same pattern as FleetReportingService)
    // ---------------------------------------------------------------------

    private async Task<List<UserProfile>> GetDriversAsync() =>
        (await GetAllAsync<UserProfile>("users"))
            .Where(u => string.Equals(u.Role, "Driver", StringComparison.OrdinalIgnoreCase))
            .ToList();

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

    private async Task<List<T>> GetBetweenAsync<T>(string collection, string dateField, DateTime fromUtc, DateTime toUtc) where T : class
    {
        QuerySnapshot snapshot = await Db.Collection(collection)
            .WhereGreaterThanOrEqualTo(dateField, fromUtc)
            .WhereLessThanOrEqualTo(dateField, toUtc)
            .GetSnapshotAsync();
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
