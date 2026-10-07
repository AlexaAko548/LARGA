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
/// <summary>Admin Cloud Storage client + the Firebase bucket to write into (registered in
/// ManagerWeb's Program.cs from the same service-account key as Firestore).</summary>
public record PhotoStorageTarget(Google.Cloud.Storage.V1.StorageClient Client, string Bucket)
{
    /// <summary>Stores an image under {folder}/ and returns its download URL - the same URL
    /// shape the mobile app's uploads get (a firebasestorage.googleapis.com link carrying a
    /// download token), so it opens straight in a browser tab and in the mobile app alike.</summary>
    public async Task<string> UploadImageAsync(string folder, byte[] image, string contentType)
    {
        string extension = contentType switch
        {
            "image/png" => "png",
            "image/webp" => "webp",
            _ => "jpg",
        };
        string objectName = $"{folder}/{DateTime.UtcNow:yyyyMMddHHmmss}.{extension}";
        string token = Guid.NewGuid().ToString();

        using var stream = new System.IO.MemoryStream(image);
        var uploaded = await Client.UploadObjectAsync(new Google.Apis.Storage.v1.Data.Object
        {
            Bucket = Bucket,
            Name = objectName,
            ContentType = contentType,
        }, stream);

        // The token Firebase's download URLs are checked against. Set in a separate update: the
        // metadata sent with the upload itself wasn't being stored (files came back with no
        // custom metadata, so the URL's token matched nothing and returned 403).
        uploaded.Metadata = new Dictionary<string, string> { ["firebaseStorageDownloadTokens"] = token };
        await Client.UpdateObjectAsync(uploaded);

        return $"https://firebasestorage.googleapis.com/v0/b/{Bucket}/o/{Uri.EscapeDataString(objectName)}?alt=media&token={token}";
    }
}

public class DriverManagementService
{
    private readonly Lazy<FirestoreDb> _dbLazy;
    private readonly Lazy<FirebaseAuth> _authLazy;
    private readonly Lazy<PhotoStorageTarget> _storageLazy;
    private readonly ILogger<DriverManagementService> _logger;

    private FirestoreDb Db => _dbLazy.Value;
    private FirebaseAuth Auth => _authLazy.Value;

    public DriverManagementService(
        Lazy<FirestoreDb> dbLazy,
        Lazy<FirebaseAuth> authLazy,
        Lazy<PhotoStorageTarget> storageLazy,
        ILogger<DriverManagementService> logger)
    {
        _dbLazy = dbLazy;
        _authLazy = authLazy;
        _storageLazy = storageLazy;
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
            // Most recent, should a driver somehow have more than one open shift.
            ShiftLog? active = activeShifts.Where(s => s.DriverId == d.UserId).OrderByDescending(s => s.ShiftStart).FirstOrDefault();
            bool isOnShift = active is not null && IsEligibleForShift(d.LicenseExpiryDate, now);
            return new DriverRosterEntry
            {
                DriverId = d.UserId,
                FullName = d.FullName,
                LicenseStatus = ComputeLicenseStatus(d.LicenseExpiryDate, now),
                IsOnShift = isOnShift,
                IsOnBreak = isOnShift && active!.IsOnBreak,
                IsLateReturn = isOnShift && active!.ShiftStart is DateTime start && now >= ShiftRules.ReturnDeadlineUtc(start),
                IsDebtFlagged = d.DebtFlaggedSince is not null,
                AssignedTaxiId = string.IsNullOrWhiteSpace(d.AssignedTaxiId) ? null : d.AssignedTaxiId,
                CurrentShiftTaxiId = isOnShift ? active!.TaxiId : null,
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
    /// not to. A "shift_schedules" document for a given driver/date only exists to record an
    /// exception to that: a day off ("DayOff"), or a temporary unit while the driver's own is
    /// under maintenance ("Substitute").</summary>
    public async Task<WeekSchedule> GetWeekScheduleAsync(DateTime weekStartUtc)
    {
        DateTime weekStart = StartOfWeek(weekStartUtc);
        DateTime weekEnd = weekStart.AddDays(7);

        List<UserProfile> drivers = await GetDriversAsync();
        List<TaxiUnit> taxis = await GetAllAsync<TaxiUnit>("taxis");
        List<ShiftSchedule> exceptions = await GetBetweenAsync<ShiftSchedule>("shift_schedules", "scheduledStartTime", weekStart, weekEnd.AddTicks(-1));
        List<MaintenanceRecord> activeJobs = await GetWhereEqualAsync<MaintenanceRecord>("maintenance_logs", "status", "InProgress");
        Func<string, DateTime, string?> maintenanceNote = BuildMaintenanceLookup(taxis, activeJobs, PhilippineTime.Now.Date);

        var rows = new List<DriverScheduleRow>();

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
                ShiftSchedule? exception = exceptions.FirstOrDefault(s => s.DriverId == driver.UserId && s.ScheduledStartTime.Date == day.Date);
                bool isDayOff = exception?.Status == "DayOff";
                bool isLicenseIneligible = IsLicenseIneligibleOn(driver.LicenseExpiryDate, day);
                string? ownUnitNote = defaultTaxiId is null ? null : maintenanceNote(defaultTaxiId, day);

                // A substitute only applies while the driver's own unit is actually in the shop
                // - once the Garage finishes the job, the cell falls back to their own unit on
                // its own, even if the substitute entry is still there. A substitute that has
                // itself gone into maintenance is ignored too, so the cell asks for a new one.
                string? substitute = exception?.Status == "Substitute"
                    && ownUnitNote is not null
                    && !string.IsNullOrWhiteSpace(exception.TaxiId)
                    && maintenanceNote(exception.TaxiId, day) is null
                        ? exception.TaxiId
                        : null;

                string? effectiveTaxiId = isDayOff || isLicenseIneligible ? null
                    : substitute ?? (ownUnitNote is null ? defaultTaxiId : null);

                row.Days.Add(new ScheduleDayCell
                {
                    Date = day,
                    TaxiId = effectiveTaxiId,
                    IsDayOff = isDayOff,
                    IsLicenseIneligible = isLicenseIneligible,
                    IsOwnUnitUnderMaintenance = ownUnitNote is not null,
                    SubstituteTaxiId = isDayOff || isLicenseIneligible ? null : substitute,
                    MaintenanceNote = ownUnitNote,
                });
            }

            rows.Add(row);
        }

        var unitsByDay = new List<int>();
        var freeByDay = new List<List<string>>();
        for (int i = 0; i < 7; i++)
        {
            DateTime day = weekStart.AddDays(i);
            var inUse = rows.Select(r => r.Days[i].TaxiId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            unitsByDay.Add(rows.Count(r => r.Days[i].TaxiId is not null));
            freeByDay.Add(taxis
                .Where(t => !string.Equals(t.Status, "Decommissioned", StringComparison.OrdinalIgnoreCase))
                .Select(t => t.TaxiId)
                .Where(id => !inUse.Contains(id) && maintenanceNote(id, day) is null)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList());
        }

        return new WeekSchedule
        {
            WeekStart = weekStart,
            Rows = rows,
            UnitsActiveByDay = unitsByDay,
            TotalTaxis = taxis.Count,
            TaxiIds = taxis.Select(t => t.TaxiId).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList(),
            FreeTaxiIdsByDay = freeByDay,
        };
    }

    /// <summary>(taxiId, day) -> a short reason if that unit is under maintenance on that
    /// calendar day, else null. Two sources, per the Garage workflow:
    /// - an In Progress Garage job for the unit, from the day it was reported through its
    ///   estimated completion date (open-ended if the Garage hasn't set one);
    /// - the taxi's own status set to "Under Maintenance" - a current state with no dates,
    ///   so it applies from today onward, not to past days.
    /// Dates are compared as Philippine calendar days.</summary>
    private static Func<string, DateTime, string?> BuildMaintenanceLookup(List<TaxiUnit> taxis, List<MaintenanceRecord> activeJobs, DateTime todayPh)
    {
        var jobsByTaxi = activeJobs
            .Where(j => !string.IsNullOrWhiteSpace(j.TaxiId))
            .GroupBy(j => j.TaxiId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var flaggedTaxis = taxis
            .Where(t => TaxiStatusRules.IsUnderMaintenance(t.Status))
            .Select(t => t.TaxiId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return (taxiId, day) =>
        {
            DateTime date = day.Date;
            if (jobsByTaxi.TryGetValue(taxiId, out List<MaintenanceRecord>? jobs))
            {
                foreach (MaintenanceRecord job in jobs)
                {
                    DateTime start = job.DateLogged.ToPhilippineTime().Date;
                    DateTime? end = job.EstimatedCompletionDate?.ToPhilippineTime().Date;
                    if (date >= start && (end is null || date <= end.Value))
                    {
                        string title = string.IsNullOrWhiteSpace(job.IssueTitle) ? "Garage job" : job.IssueTitle;
                        return end is null ? $"{title} - no finish date yet" : $"{title} - until {end:MMM d}";
                    }
                }
            }

            return flaggedTaxis.Contains(taxiId) && date >= todayPh ? "Marked Under Maintenance" : null;
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
    /// will actually have on that date, not just today's snapshot. A driver whose license
    /// hasn't been set yet (no expiry date on file) can't be scheduled on any date either -
    /// not even by the planner's default of every day on their own unit - until the manager
    /// records a valid license in their profile.</summary>
    private static bool IsLicenseIneligibleOn(DateTime? licenseExpiry, DateTime date) =>
        !licenseExpiry.HasValue || date.Date >= licenseExpiry.Value.AddMonths(-1).Date;

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
        if (profile.LicenseExpiryDate is null)
        {
            throw new InvalidOperationException("This driver's license hasn't been set yet - add their license in their profile before scheduling them.");
        }
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

    /// <summary>Gives the driver a temporary unit for this one day because their own is under
    /// maintenance. Checked server-side against a fresh schedule, not just the page's
    /// dropdown: the driver's unit must really be in the shop that day, and the substitute
    /// must be free (not under maintenance, not driven by anyone else).</summary>
    public async Task AssignSubstituteAsync(string driverId, DateTime dateUtc, string taxiId)
    {
        await EnsureLicenseEligibleOnAsync(driverId, dateUtc);

        WeekSchedule week = await GetWeekScheduleAsync(dateUtc);
        int dayIndex = (dateUtc.Date - week.WeekStart.Date).Days;
        ScheduleDayCell? cell = week.Rows.FirstOrDefault(r => r.DriverId == driverId)?.Days[dayIndex];

        if (cell is null || !cell.IsOwnUnitUnderMaintenance)
        {
            throw new InvalidOperationException("This driver's own unit isn't under maintenance on this date - no substitute needed.");
        }

        bool keepingSameSubstitute = string.Equals(cell.SubstituteTaxiId, taxiId, StringComparison.OrdinalIgnoreCase);
        if (!keepingSameSubstitute && !week.FreeTaxiIdsByDay[dayIndex].Contains(taxiId, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{taxiId} isn't free on {dateUtc:MMM d} - it's under maintenance or another driver has it.");
        }

        var schedule = new ShiftSchedule
        {
            DriverId = driverId,
            TaxiId = taxiId,
            ScheduledStartTime = DateTime.SpecifyKind(dateUtc.Date, DateTimeKind.Utc),
            Status = "Substitute",
        };
        await Db.Collection("shift_schedules").Document(ScheduleDocId(driverId, dateUtc)).SetAsync(schedule, SetOptions.Overwrite);
    }

    /// <summary>Removes this day's exception (day off or substitute), reverting the cell
    /// back to its default: working, with the driver's permanently assigned unit.</summary>
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
                List<HandoverChecklist> shiftChecklists = checklists.Where(c => BelongsTo(c.ShiftId, s)).ToList();
                int defectCount = shiftChecklists.Sum(c => EvaluateChecklist(c).Items.Count(i => i.Passed == false));

                return new ShiftLogEntry
                {
                    ShiftId = s.DocumentId,
                    ShiftStart = s.ShiftStart,
                    DriverId = s.DriverId,
                    DriverName = drivers.FirstOrDefault(d => d.UserId == s.DriverId)?.FullName ?? s.DriverId,
                    TaxiId = s.TaxiId,
                    Status = ShiftLogStatus(s),
                    HasPreShiftChecklist = shiftChecklists.Any(c => c.ChecklistType == ChecklistType.PreShift),
                    HasEndShiftChecklist = shiftChecklists.Any(c => c.ChecklistType == ChecklistType.EndShift),
                    DefectCount = defectCount,
                };
            })
            .ToList();
    }

    /// <summary>What Shift Logs shows: the stored status, except an open shift is "Late Return"
    /// once past its 10:00 PM return time (ShiftRules), or "On Break" while paused.</summary>
    private static string ShiftLogStatus(ShiftLog s)
    {
        if (s.Status != "Active")
        {
            return s.Status;
        }
        if (s.ShiftStart is DateTime start && DateTime.UtcNow >= ShiftRules.ReturnDeadlineUtc(start))
        {
            return "Late Return";
        }
        return s.IsOnBreak ? "On Break" : "Active";
    }

    /// <summary>Other collections reference a shift by its shiftId field, which should equal
    /// the document ID - but shifts clocked in by older mobile builds got a made-up
    /// "SHIFT_yyyyMMdd_nnn" value instead, while their fuel logs/checklists point at the
    /// document ID. Accepting either keeps those older shifts linked up.</summary>
    private static bool BelongsTo(string? referencedShiftId, ShiftLog shift) =>
        !string.IsNullOrEmpty(referencedShiftId)
        && (referencedShiftId == shift.DocumentId || referencedShiftId == shift.ShiftId);

    private static List<string> IdsOf(ShiftLog shift) =>
        new[] { shift.DocumentId, shift.ShiftId }.Where(id => !string.IsNullOrEmpty(id)).Distinct().ToList();

    /// <param name="shiftId">The shift's Firestore document ID (ShiftLogEntry.ShiftId).</param>
    public async Task<ShiftChecklists?> GetShiftChecklistsAsync(string shiftId)
    {
        DocumentSnapshot shiftDoc = await Db.Collection("shifts").Document(shiftId).GetSnapshotAsync();
        if (!shiftDoc.Exists)
        {
            return null;
        }

        ShiftLog shift = shiftDoc.ConvertTo<ShiftLog>();
        List<UserProfile> drivers = await GetDriversAsync();
        List<HandoverChecklist> checklists = await GetWhereInAsync<HandoverChecklist>("handover_checklists", "shiftId", IdsOf(shift));

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
            .Where(s => s.DocumentId != excludeShiftId)
            .OrderByDescending(s => s.ShiftStart)
            .SelectMany(IdsOf)
            .Distinct()
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

    // Firestore stores these raw signals (tireCondition, oilLevel, coolantLevel,
    // lightsCondition, interiorCleanliness, exteriorScratches, fuelVerification) - oil+coolant are combined
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
        };

        // Only recorded by the mobile checklist - older/seeded documents don't have it, and
        // showing those as a failed check would be wrong.
        if (c.LightsCondition.HasValue)
        {
            items.Add(new() { Label = "Lights & Signals", Passed = c.LightsCondition.Value });
        }

        items.AddRange(new List<ChecklistItemResult>
        {
            new() { Label = "Interior Cleanliness & Comfort", Passed = c.InteriorCleanliness },
            new() { Label = "Checked exterior Scratches / Dents", Passed = c.ExteriorScratches },
            new()
            {
                Label = "Fuel Verification",
                Passed = null,
                ValueText = c.FuelVerification == FuelVerification.BelowHalfTank ? "Below half-tank" : "Half-tank",
            },
        });

        // The mobile checklist takes an odometer photo rather than an exterior one, so show
        // that in the first slot whenever there's no exterior photo. The mobile client writes
        // "" (not null) for a photo that failed to upload, hence the IsNullOrWhiteSpace checks.
        bool hasScratchesPhoto = !string.IsNullOrWhiteSpace(c.ScratchesPhotoUrl);
        string? primaryPhoto = hasScratchesPhoto ? c.ScratchesPhotoUrl : c.OdometerPhotoUrl;

        return new ChecklistDetail
        {
            ChecklistType = c.ChecklistType == ChecklistType.EndShift ? "Post-Shift" : "Pre-Shift",
            Timestamp = c.Timestamp,
            Items = items,
            PrimaryPhotoUrl = string.IsNullOrWhiteSpace(primaryPhoto) ? null : primaryPhoto,
            PrimaryPhotoLabel = hasScratchesPhoto ? "Exterior / Scratches Photo" : "Odometer Photo",
            SecondaryPhotoUrl = string.IsNullOrWhiteSpace(c.FuelDashboardUrl) ? null : c.FuelDashboardUrl,
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
        HashSet<string> shiftIds = driverShifts.SelectMany(IdsOf).ToHashSet();

        List<BoundaryPayment> allPayments = await GetAllAsync<BoundaryPayment>("boundary_payments");
        List<MaintenanceRecord> allMaintenance = await GetAllAsync<MaintenanceRecord>("maintenance_logs");

        List<ShiftSchedule> exceptions = await GetWhereEqualAsync<ShiftSchedule>("shift_schedules", "driverId", driverId);
        DriverPerformance performance = DriverPerformanceCalculator.Calculate(
            profile, driverShifts, exceptions, allMaintenance, allPayments.Where(p => shiftIds.Contains(p.ShiftId)).ToList(),
            await GetDefaultBoundaryRateAsync(), PhilippineTime.Now.Date.AddDays(1 - PerformanceWindowDays), DateTime.UtcNow);

        ShiftLog? activeShift = driverShifts.FirstOrDefault(s => s.Status == "Active");
        DateTime now = DateTime.UtcNow;

        return new DriverProfileDetail
        {
            DriverId = profile.UserId,
            FullName = profile.FullName,
            Email = profile.Email,
            PhoneNumber = profile.PhoneNumber,
            Address = profile.Address,
            DateJoined = profile.DateJoined,
            IsOnShift = activeShift is not null && IsEligibleForShift(profile.LicenseExpiryDate, now),
            // Always the profile's own assignment - the Edit Details form pre-fills from this,
            // so it must never be the active shift's unit (saving would silently reassign the
            // driver to whatever they happened to be driving).
            AssignedTaxiId = string.IsNullOrWhiteSpace(profile.AssignedTaxiId) ? null : profile.AssignedTaxiId,
            CurrentShiftTaxiId = activeShift?.TaxiId,
            LicenseNumber = profile.LicenseNumber,
            LicenseClassification = profile.LicenseClassification,
            LicenseRestrictionCode = profile.LicenseRestrictionCode,
            LicenseStatus = ComputeLicenseStatus(profile.LicenseExpiryDate, now),
            LicenseExpiryDate = profile.LicenseExpiryDate,
            LtoIdPhotoUrl = profile.LtoIdPhotoUrl,
            ProfileImageUrl = profile.ProfileImageUrl,
            Performance = performance,
            ManagerNote = profile.ManagerNote,
        };
    }

    /// <summary>How far back the profile's performance figures look.</summary>
    public const int PerformanceWindowDays = 30;

    private async Task<decimal> GetDefaultBoundaryRateAsync()
    {
        try
        {
            DocumentSnapshot snapshot = await Db.Collection("system_configs").Document("global").GetSnapshotAsync();
            if (snapshot.Exists && snapshot.ConvertTo<SystemConfig>().DefaultBoundaryRate is double rate && rate > 0)
            {
                return (decimal)rate;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read the default boundary rate");
        }
        return ShiftRules.DefaultBoundaryRate;
    }

    public async Task UpdateDriverProfileAsync(
        string driverId,
        string? phoneNumber,
        string? address,
        string? licenseNumber,
        string? licenseClassification,
        string? licenseRestrictionCode,
        DateTime? licenseExpiryDate,
        string? assignedTaxiId,
        string? ltoIdPhotoUrl = null,
        string? profileImageUrl = null)
    {
        // Same rules the Edit Details form checks (InputValidator) - refused here too so nothing
        // malformed is ever stored. Phone and license number are saved in one standard format.
        string? error = InputValidator.ValidatePhilippineMobile(phoneNumber)
            ?? InputValidator.ValidateText(address, "the address", 200)
            ?? InputValidator.ValidateLicenseNumber(licenseNumber)
            ?? InputValidator.ValidateLicenseClassification(licenseClassification)
            ?? InputValidator.ValidateRestrictionCode(licenseRestrictionCode)
            ?? InputValidator.ValidateLicenseExpiry(licenseExpiryDate, DateTime.UtcNow);
        if (error is not null)
        {
            throw new ArgumentException(error);
        }

        var updates = new Dictionary<string, object>
        {
            ["phoneNumber"] = InputValidator.NormalizePhilippineMobile(phoneNumber)!,
            ["address"] = InputValidator.NormalizeSpaces(address),
            ["licenseNumber"] = InputValidator.NormalizeLicenseNumber(licenseNumber) ?? string.Empty,
            ["licenseClassification"] = InputValidator.NormalizeLicenseClassification(licenseClassification),
            ["licenseRestrictionCode"] = InputValidator.NormalizeSpaces(licenseRestrictionCode),
            ["assignedTaxiId"] = assignedTaxiId ?? string.Empty,
        };

        if (licenseExpiryDate.HasValue)
        {
            // The page's <input type="date"> yields an Unspecified-kind DateTime, which the
            // Firestore SDK won't serialize - store the calendar date as UTC midnight, the same
            // way schedule days are represented (IsLicenseIneligibleOn compares .Date values).
            updates["licenseExpiryDate"] = DateTime.SpecifyKind(licenseExpiryDate.Value.Date, DateTimeKind.Utc);
        }

        // Only when a new license photo was scanned - otherwise keep whatever is on file.
        if (!string.IsNullOrWhiteSpace(ltoIdPhotoUrl))
        {
            updates["ltoIdPhotoUrl"] = ltoIdPhotoUrl;
        }

        // Same rule for the profile picture: only when a face was cropped from a new scan.
        if (!string.IsNullOrWhiteSpace(profileImageUrl))
        {
            updates["profileImageUrl"] = profileImageUrl;
        }

        await Db.Collection("users").Document(driverId).UpdateAsync(updates);
    }

    /// <summary>Stores a scanned LTO license photo in Firebase Storage and returns its
    /// download URL (for users/{id}.ltoIdPhotoUrl).</summary>
    public Task<string> UploadLtoIdPhotoAsync(string driverId, byte[] photo, string contentType) =>
        _storageLazy.Value.UploadImageAsync($"lto_ids/{driverId}", photo, contentType);

    /// <summary>Stores the face crop from a license scan and returns its download URL (for
    /// users/{id}.profileImageUrl). Same lto_ids/{driverId} folder as the license photo.</summary>
    public Task<string> UploadProfileFaceAsync(string driverId, byte[] face) =>
        _storageLazy.Value.UploadImageAsync($"lto_ids/{driverId}", face, "image/jpeg");

    public async Task SetManagerNoteAsync(string driverId, string note)
    {
        if (InputValidator.ValidateText(note, "the note", 500) is string error)
        {
            throw new ArgumentException(error);
        }

        await Db.Collection("users").Document(driverId).UpdateAsync("managerNote", note);
    }

    // ---------------------------------------------------------------------
    // Driver account management (manager-provisioned - drivers never self-register)
    // ---------------------------------------------------------------------

    public async Task<CreateDriverResult> CreateDriverAsync(string fullName, string phoneNumber, string? temporaryPassword, string? assignedTaxiId = null, string? email = null, string? actorUserId = null)
    {
        fullName = InputValidator.NormalizeSpaces(fullName);
        string? inputError = InputValidator.ValidateFullName(fullName)
            ?? InputValidator.ValidatePhilippineMobile(phoneNumber)
            ?? InputValidator.ValidatePassword(temporaryPassword, required: false);
        if (inputError is not null)
        {
            return new CreateDriverResult { Ok = false, ErrorMessage = inputError };
        }
        phoneNumber = InputValidator.NormalizePhilippineMobile(phoneNumber)!;

        string password = string.IsNullOrWhiteSpace(temporaryPassword) ? GenerateTemporaryPassword() : temporaryPassword;

        // Drivers sign in with email+password (same as the mobile app's login screen). The
        // driver's own email is preferred - the mobile app's Forgot Password can only reach a
        // real inbox - but it's optional: with none given, a placeholder is generated and the
        // manager relays it, with the password, to the driver directly.
        string loginEmail;
        if (string.IsNullOrWhiteSpace(email))
        {
            loginEmail = GenerateDriverEmail(fullName);
        }
        else
        {
            loginEmail = email.Trim().ToLowerInvariant();
            if (!IsValidEmail(loginEmail))
            {
                return new CreateDriverResult { Ok = false, ErrorMessage = "Enter a valid email address, e.g. juan.delacruz@gmail.com - or leave it blank to generate one." };
            }
        }

        UserRecord userRecord;
        try
        {
            userRecord = await Auth.CreateUserAsync(new UserRecordArgs
            {
                Email = loginEmail,
                Password = password,
                DisplayName = fullName,
            });
        }
        catch (FirebaseAuthException ex) when (ex.AuthErrorCode == AuthErrorCode.EmailAlreadyExists)
        {
            return new CreateDriverResult { Ok = false, ErrorMessage = $"{loginEmail} is already used by another account." };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create Firebase Auth account for new driver {FullName}", fullName);
            return new CreateDriverResult { Ok = false, ErrorMessage = ex.Message };
        }

        var profile = new UserProfile
        {
            FullName = fullName,
            Email = loginEmail,
            PhoneNumber = phoneNumber,
            // Their permanent unit - the Schedule Planner fills every working day with it.
            AssignedTaxiId = string.IsNullOrWhiteSpace(assignedTaxiId) ? string.Empty : assignedTaxiId,
            Role = "Driver",
            DateJoined = DateTime.UtcNow,
            MustChangePassword = true,
        };

        try
        {
            // Profile and its audit entry commit together; if either fails, the Auth account
            // is rolled back below, so provisioning is never logged without a profile (or vice versa).
            DocumentReference profileDoc = Db.Collection("users").Document(userRecord.Uid);
            DocumentReference auditDoc = Db.Collection("audit_logs").Document();
            var audit = new AuditLog
            {
                UserId = actorUserId ?? string.Empty,
                ActionType = "DriverAccountProvisioned",
                AuditLogDetails = $"Provisioned driver account for '{fullName}' (driver ID: {userRecord.Uid}).",
                Timestamp = DateTime.UtcNow,
            };

            WriteBatch batch = Db.StartBatch();
            batch.Set(profileDoc, profile, SetOptions.Overwrite);
            batch.Set(auditDoc, audit);
            await batch.CommitAsync();
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
            GeneratedEmail = loginEmail,
            TemporaryPassword = password,
        };
    }

    public async Task<ResetPasswordResult> ResetDriverPasswordAsync(string driverId, string? newPassword)
    {
        if (InputValidator.ValidatePassword(newPassword, required: false) is string passwordError)
        {
            return new ResetPasswordResult { Ok = false, ErrorMessage = passwordError };
        }
        string password = string.IsNullOrWhiteSpace(newPassword) ? GenerateTemporaryPassword() : newPassword;

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

    /// <summary>Changes the email a driver signs in to the mobile app with - both the Firebase
    /// Auth login and users/{id}.email, kept in step. Drivers are created with a placeholder
    /// address (see GenerateDriverEmail), which can't receive the mobile app's Forgot Password
    /// email; switching to the driver's real address makes that flow work. Their password
    /// doesn't change.</summary>
    public async Task<ResetPasswordResult> UpdateDriverLoginEmailAsync(string driverId, string newEmail)
    {
        string email = (newEmail ?? string.Empty).Trim().ToLowerInvariant();
        if (!IsValidEmail(email))
        {
            return new ResetPasswordResult { Ok = false, ErrorMessage = "Enter a valid email address, e.g. juan.delacruz@gmail.com." };
        }

        UserRecord current;
        try
        {
            current = await Auth.GetUserAsync(driverId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't load Auth account for driver {DriverId}", driverId);
            return new ResetPasswordResult { Ok = false, ErrorMessage = "This driver has no mobile app login account." };
        }

        if (string.Equals(current.Email, email, StringComparison.OrdinalIgnoreCase))
        {
            return new ResetPasswordResult { Ok = true };
        }

        try
        {
            await Auth.UpdateUserAsync(new UserRecordArgs { Uid = driverId, Email = email, EmailVerified = false });
        }
        catch (FirebaseAuthException ex) when (ex.AuthErrorCode == AuthErrorCode.EmailAlreadyExists)
        {
            return new ResetPasswordResult { Ok = false, ErrorMessage = $"{email} is already used by another account." };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to change login email for driver {DriverId}", driverId);
            return new ResetPasswordResult { Ok = false, ErrorMessage = ex.Message };
        }

        try
        {
            await Db.Collection("users").Document(driverId).UpdateAsync("email", email);
        }
        catch (Exception ex)
        {
            // Put the login back so the two never disagree about which email is the driver's.
            _logger.LogWarning(ex, "Profile email write failed for {DriverId}; reverting Auth email", driverId);
            try { await Auth.UpdateUserAsync(new UserRecordArgs { Uid = driverId, Email = current.Email }); }
            catch (Exception revertEx) { _logger.LogWarning(revertEx, "Also failed to revert Auth email for {DriverId}", driverId); }
            return new ResetPasswordResult { Ok = false, ErrorMessage = "Could not save the new email. Please try again." };
        }

        return new ResetPasswordResult { Ok = true };
    }

    // Shape check only (something@domain.tld) - Firebase Auth does the real validation.
    private static bool IsValidEmail(string email) => InputValidator.IsValidEmail(email);

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
