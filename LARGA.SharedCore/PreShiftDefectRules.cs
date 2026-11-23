using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using LARGA.Shared.Models.Entities;

namespace LARGA.SharedCore;

/// <summary>
/// Where a defect the driver files during the pre-shift inspection is listed. The phone saves it straight
/// away (maintenance_logs, status "Reported") and the clock-in request lists it in defectReportIds. While
/// that request waits for the manager it belongs on Driver &amp; Shifts only, where the manager decides on it.
/// Once the manager has decided, it's listed in the Garage's Pending Driver Reports like any other report:
/// denied (ClockInApprovalService.DenyAsync escalates it) or approved - the unit went out anyway, so the
/// defect still needs scheduling or dismissing later. Driver &amp; Shifts lists only pending clock-ins, so
/// holding an approved one back here would leave its defect on no manager screen at all.
///
/// Display only: nothing here changes a record or its status. Reports linked to a decided or cancelled
/// request, and ones not linked to a request at all (during / after the shift), are listed as before.
/// </summary>
public static class PreShiftDefectRules
{
    /// <summary>clockin_requests statuses whose defects stay off the Garage list: only a request still waiting
    /// for the manager. One equality query, and the number of pending requests stays small.</summary>
    public static readonly IReadOnlyList<string> HeldStatuses = new[] { ClockInRequest.Pending };

    /// <summary>The maintenance_logs IDs in a request's defectReportIds - written comma-separated, but a
    /// list is read too, and blanks and spaces are dropped.</summary>
    public static IEnumerable<string> DefectIds(object? defectReportIds)
    {
        IEnumerable<string?> parts = defectReportIds switch
        {
            null => Array.Empty<string>(),
            string text => text.Split(','),
            IEnumerable list => list.Cast<object?>().Select(item => item?.ToString()),
            _ => Array.Empty<string>(),
        };
        return parts.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!.Trim());
    }

    /// <summary>The defects held by requests in <see cref="HeldStatuses"/>.</summary>
    public static HashSet<string> HeldDefectIds(IEnumerable<(string? Status, object? DefectReportIds)> requests) =>
        requests
            .Where(r => HeldStatuses.Contains(r.Status ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            .SelectMany(r => DefectIds(r.DefectReportIds))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>The Garage's Pending Driver Reports: every "Reported" record not held by a clock-in request.</summary>
    public static IEnumerable<MaintenanceRecord> PendingGarageReports(IEnumerable<MaintenanceRecord> records, IReadOnlySet<string> heldDefectIds) =>
        records.Where(r => r.Status == WorkOrderRules.Reported && !heldDefectIds.Contains(r.MaintenanceId));
}
