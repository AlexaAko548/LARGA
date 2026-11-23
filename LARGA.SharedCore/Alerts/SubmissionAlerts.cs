using System;
using System.Globalization;
using System.Threading.Tasks;
using Google.Cloud.Firestore;

namespace LARGA.SharedCore.Alerts;

/// <summary>
/// SRS REQ-4.3-7 / REQ-4.4-8: a manager alert (system_alerts) for every new driver defect report
/// (maintenance_logs, status "Reported") and every fuel submission (fuel_logs), written by ManagerWeb's
/// SubmissionAlertService. IDs are deterministic (one per source document), so a restart or a second
/// instance never duplicates one. The alert is marked read once the item has been reviewed: the
/// report leaves "Reported" (ticket created / dismissed), or the fuel log is verified, flagged or
/// resolved by a manager.
/// </summary>
public static class SubmissionAlerts
{
    public const string DefectReportedType = "DefectReported";
    public const string FuelSubmittedType = "FuelSubmitted";

    /// <summary>FCM data "type" of the push sent to Managers/Assistant Managers for a defect report.</summary>
    public const string DefectPushType = "defect_report";

    /// <summary>On ManagerWeb's first look after (re)starting, only items this recent get an alert -
    /// older ones were either alerted already or predate this feature.</summary>
    public static readonly TimeSpan Lookback = TimeSpan.FromHours(24);

    public static string DefectAlertId(string maintenanceId) => $"defect_{maintenanceId}";
    public static string FuelSubmittedAlertId(string fuelLogId) => $"fuelsub_{fuelLogId}";

    /// <summary>Whether an item created at <paramref name="createdUtc"/> is recent enough to alert on.</summary>
    public static bool IsRecent(DateTime? createdUtc, DateTime nowUtc) =>
        createdUtc is DateTime created && created >= nowUtc - Lookback;

    public static string DefectMessage(string driverName, string taxiId, string issueTitle, string? priority)
    {
        string issue = string.IsNullOrWhiteSpace(issueTitle) ? "a defect" : $"\"{issueTitle.Trim()}\"";
        string level = string.IsNullOrWhiteSpace(priority) ? string.Empty : $" ({priority} priority)";
        return $"{driverName} reported {issue} on {taxiId}{level}.";
    }

    public static string FuelMessage(string driverName, string taxiId, double liters, double cost, string? station)
    {
        string where = string.IsNullOrWhiteSpace(station) ? string.Empty : $" at {station.Trim()}";
        return string.Create(CultureInfo.InvariantCulture,
            $"{driverName} submitted a fuel report for {taxiId}: {liters:0.##} L, PHP {cost:N2}{where}.");
    }

    /// <summary>Marks a submission alert read if it exists (an item from before this feature has none).
    /// Never throws - the review itself already saved.</summary>
    public static async Task MarkReviewedAsync(FirestoreDb db, string alertId)
    {
        try
        {
            DocumentReference docRef = db.Collection(AlertFeed.Collection).Document(alertId);
            await docRef.UpdateAsync("isRead", true);
        }
        catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.NotFound)
        {
            // No alert for this item.
        }
        catch (Exception)
        {
            // Best effort.
        }
    }
}
