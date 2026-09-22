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
                IsOnShift = active is not null && IsEligibleForShift(d.LicenseExpiryDate, now),
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

        // Expiring = within 3 calendar months of today; anything further out is Valid.
        return expiry.Value <= now.AddMonths(3) ? LicenseStatus.Expiring : LicenseStatus.Valid;
    }

    // A driver isn't eligible to be shown/counted as "On Shift" once their license is within
    // 1 month of expiring (or already expired) - this doesn't touch the underlying SHIFT_LOG
    // document (nothing here ends a real active shift), it only overrides the *computed*
    // on-shift status this page reports, the same "computed, not stored" pattern already used
    // for LicenseStatus/live fleet status elsewhere. A manager still sees the real shift in
    // Shift Logs; the roster/profile just won't call the driver On Shift while ineligible.
    private static bool IsEligibleForShift(DateTime? licenseExpiry, DateTime now) =>
        licenseExpiry is null || licenseExpiry.Value > now.AddMonths(1);

    // ---------------------------------------------------------------------
    // Schedule Planner
    // ---------------------------------------------------------------------

    /// <summary>Every day defaults to "working the driver's permanently assigned unit"
    /// (UserProfile.AssignedTaxiId) - under BLM Taxi's boundary system, one driver has one
    /// unit assigned to them permanently, and works daily unless there's a specific reason
    /// not to, so working is the norm and a manager should only ever have to touch the one
    /// exception that matters: a day off. A "shift_schedules" document for a given
    /// driver/date only exists to record that exception - there is no per-day unit swap.</summary>
    public async Task<WeekSchedule> GetWeekScheduleAsync(DateTime weekStartUtc)
    {
        DateTime weekStart = StartOfWeek(weekStartUtc);
        DateTime weekEnd = weekStart.AddDays(7);

        List<UserProfile> drivers = await GetDriversAsync();
        List<TaxiUnit> taxis = await GetAllAsync<TaxiUnit>("taxis");
        List<ShiftSchedule> dayOffs = await GetBetweenAsync<ShiftSchedule>("shift_schedules", "scheduledStartTime", weekStart, weekEnd.AddTicks(-1));

        var rows = new List<DriverScheduleRow>();
        var unitsByDay = new int[7];

        foreach (UserProfile driver in drivers)
        {
            string? defaultTaxiId = string.IsNullOrWhiteSpace(driver.AssignedTaxiId) ? null : driver.AssignedTaxiId;

            var row = new DriverScheduleRow
            {
                DriverId = driver.UserId,
                FullName = driver.FullName,
                LicenseStatus = ComputeLicenseStatus(driver.LicenseExpiryDate, DateTime.UtcNow),
                DefaultTaxiId = defaultTaxiId,
            };

            for (int i = 0; i < 7; i++)
            {
                DateTime day = weekStart.AddDays(i);
                bool isDayOff = dayOffs.Any(s => s.DriverId == driver.UserId && s.ScheduledStartTime.Date == day.Date && s.Status == "DayOff");
                bool isLicenseIneligible = IsLicenseIneligibleOn(driver.LicenseExpiryDate, day);
                string? effectiveTaxiId = isDayOff || isLicenseIneligible ? null : defaultTaxiId;

                row.Days.Add(new ScheduleDayCell
                {
                    Date = day,
                    TaxiId = effectiveTaxiId,
                    IsDayOff = isDayOff,
                    IsLicenseIneligible = isLicenseIneligible,
                });

                if (effectiveTaxiId is not null)
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

    /// <summary>A driver can never be scheduled - working or day-off, it doesn't matter which -
    /// on a date that falls on/after their license expiry, or within one month before it. Mirrors
    /// IsEligibleForShift's 1-month cutoff, but evaluated per calendar day rather than just "now":
    /// a manager paging the planner forward to a future week sees the same eligibility the driver
    /// will actually have on that date, not just today's snapshot. A driver with no expiry date
    /// on file is never restricted by this rule.</summary>
    private static bool IsLicenseIneligibleOn(DateTime? licenseExpiry, DateTime date) =>
        licenseExpiry.HasValue && date.Date >= licenseExpiry.Value.AddMonths(-1).Date;

    /// <summary>Server-side backstop for the same rule the Schedule Planner UI enforces by
    /// disabling the cell - looked up fresh rather than trusting a value the caller might pass
    /// in, since this is a real business rule (an unlicensed/soon-to-be-unlicensed driver must
    /// never be scheduled), not just a UI convenience.</summary>
    private async Task EnsureLicenseEligibleOnAsync(string driverId, DateTime date)
    {
        DocumentSnapshot doc = await Db.Collection("users").Document(driverId).GetSnapshotAsync();
        if (!doc.Exists)
        {
            return;
        }

        UserProfile profile = doc.ConvertTo<UserProfile>();
        if (IsLicenseIneligibleOn(profile.LicenseExpiryDate, date))
        {
            throw new InvalidOperationException("This driver's license is expired or within one month of expiring on this date - they cannot be scheduled.");
        }
    }

    /// <summary>Marks this driver off for this day - the one exception a manager actually
    /// needs to set, since every day otherwise defaults to working.</summary>
    public async Task MarkDayOffAsync(string driverId, DateTime dateUtc)
    {
        await EnsureLicenseEligibleOnAsync(driverId, dateUtc);

        var schedule = new ShiftSchedule
        {
            DriverId = driverId,
            TaxiId = string.Empty,
            ScheduledStartTime = DateTime.SpecifyKind(dateUtc.Date, DateTimeKind.Utc),
            Status = "DayOff",
        };
        await Db.Collection("shift_schedules").Document(ScheduleDocId(driverId, dateUtc)).SetAsync(schedule, SetOptions.Overwrite);
    }

    /// <summary>Removes the day-off exception for this day, reverting the cell back to its
    /// default: working, with the driver's permanently assigned unit.</summary>
    public async Task ClearScheduleAsync(string driverId, DateTime dateUtc)
    {
        await EnsureLicenseEligibleOnAsync(driverId, dateUtc);

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
            PreviousChecklists = await GetPreviousChecklistsAsync(shift.TaxiId, shiftId),
            DamageHistory = await GetDamageHistoryAsync(shift.TaxiId),
        };
    }

    /// <summary>The taxi unit's most recent past inspection checklists (excluding this shift),
    /// for the manager to compare against while reviewing this shift's checklist. Single
    /// equality filter on taxiId (auto-indexed) rather than a composite query, same
    /// index-avoidance pattern used elsewhere in this codebase.</summary>
    private async Task<List<ChecklistHistoryEntry>> GetPreviousChecklistsAsync(string taxiId, string excludeShiftId)
    {
        List<ShiftLog> taxiShifts = await GetWhereEqualAsync<ShiftLog>("shifts", "taxiId", taxiId);
        List<string> otherShiftIds = taxiShifts
            .Where(s => s.ShiftId != excludeShiftId && !string.IsNullOrEmpty(s.ShiftId))
            .Select(s => s.ShiftId)
            .ToList();

        if (otherShiftIds.Count == 0)
        {
            return new List<ChecklistHistoryEntry>();
        }

        // Firestore caps WhereIn at 30 values - a unit with more shift history than that
        // just has its oldest shifts excluded from this lookback, which is fine since only
        // the most recent few checklists are ever shown here anyway.
        List<HandoverChecklist> taxiChecklists = await GetWhereInAsync<HandoverChecklist>(
            "handover_checklists", "shiftId", otherShiftIds.Take(30).ToList());

        return taxiChecklists
            .OrderByDescending(c => c.Timestamp)
            .Take(6)
            .Select(c =>
            {
                ChecklistDetail detail = EvaluateChecklist(c);
                return new ChecklistHistoryEntry
                {
                    Timestamp = c.Timestamp,
                    ChecklistType = detail.ChecklistType,
                    PassedCount = detail.PassedCount,
                    TotalCheckableCount = detail.TotalCheckableCount,
                };
            })
            .ToList();
    }

    /// <summary>The taxi unit's past accident/damage maintenance records, for reference
    /// alongside its checklist history.</summary>
    private async Task<List<DamageHistoryEntry>> GetDamageHistoryAsync(string taxiId)
    {
        List<MaintenanceRecord> records = await GetWhereEqualAsync<MaintenanceRecord>("maintenance_logs", "taxiId", taxiId);
        return records
            .Where(m => m.MaintenanceType == MaintenanceType.AccidentCorrection)
            .OrderByDescending(m => m.DateLogged)
            .Take(5)
            .Select(m => new DamageHistoryEntry
            {
                DateLogged = m.DateLogged,
                IssueTitle = m.IssueTitle,
                Status = m.Status,
            })
            .ToList();
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
            IsOnShift = activeShift is not null && IsEligibleForShift(profile.LicenseExpiryDate, now),
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

    private async Task<List<T>> GetWhereInAsync<T>(string collection, string field, List<string> values) where T : class
    {
        QuerySnapshot snapshot = await Db.Collection(collection).WhereIn(field, values).GetSnapshotAsync();
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
