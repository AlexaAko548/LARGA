using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Maui.Controls;
using Plugin.Firebase.Firestore;

namespace LARGA.MobileApp.ViewModels.Manager;

public class AlertCenterViewModel : BindableObject
{
    public ObservableCollection<AlertItem> Alerts { get; } = new();

    public ICommand DismissAlertCommand { get; }
    public ICommand ApproveShiftCommand { get; }
    public ICommand DenyShiftCommand { get; }
    public ICommand CallDriverCommand { get; }
    public ICommand MessageDriverCommand { get; }
    public ICommand ViewLocationCommand { get; }

    public AlertCenterViewModel()
    {
        DismissAlertCommand = new Command<AlertItem>(async (alert) =>
        {
            if (alert == null) return;
            Alerts.Remove(alert);

            // Real (non-mock) alerts carry a Firestore doc id - persist the dismissal as
            // "read" so it doesn't reappear next time this page loads. Mock SOS/Fuel/
            // ShiftApproval cards have no AlertId, so this is a no-op for them.
            if (!string.IsNullOrEmpty(alert.AlertId))
            {
                await MarkAlertReadAsync(alert.AlertId);
            }
        });

        ApproveShiftCommand = new Command<AlertItem>(async (alert) =>
        {
            // TODO: update ShiftLog status to Approved in Firestore
            if (alert != null) Alerts.Remove(alert);
        });

        DenyShiftCommand = new Command<AlertItem>(async (alert) =>
        {
            // TODO: update ShiftLog status to Denied, link MaintenanceRecord, notify driver
            if (alert != null) Alerts.Remove(alert);
        });

        CallDriverCommand = new Command<AlertItem>(async (alert) =>
        {
            // TODO: trigger phone call
        });

        MessageDriverCommand = new Command<AlertItem>(async (alert) =>
        {
            // TODO: navigate to message thread
        });

        ViewLocationCommand = new Command<AlertItem>(async (alert) =>
        {
            // TODO: navigate to fleet map centered on driver
        });

        LoadMockAlerts();
        _ = LoadIdleAlertsAsync();
    }

    private void LoadMockAlerts()
    {
        Alerts.Add(new AlertItem
        {
            Type = AlertType.Sos,
            DriverName = "Juan Dela Cruz · Unit 02",
            Subtitle = "University of San Carlos - Downtown",
            Timestamp = "10:45 AM"
        });

        Alerts.Add(new AlertItem
        {
            Type = AlertType.FuelDiscrepancy,
            DriverName = "Maria Santos · Unit 03",
            Subtitle = "Unit started with < 50% fuel.",
            Timestamp = "09:22 AM"
        });

        Alerts.Add(new AlertItem
        {
            Type = AlertType.ShiftApproval,
            DriverName = "Juan Dela Cruz · Unit 02",
            Timestamp = "10:45 AM",
            FailedItem = "Tire Condition",
            Priority = "High",
            DriverDescription = "Right passenger side rear tire is completely flat. Found a nail in it during walk-around."
        });
    }

    /// <summary>Loads real, unread "driver idle" alerts from `system_alerts` (raised
    /// server-side by ManagerWeb's IdleAlertMonitorService) and appends them alongside the
    /// mock SOS/Fuel/ShiftApproval cards above. Single equality filter (type == "DriverIdle")
    /// only - no composite index needed - with the isRead filter and timestamp ordering done
    /// client-side, matching this project's usual approach to keeping Firestore queries
    /// index-free.</summary>
    private async Task LoadIdleAlertsAsync()
    {
        try
        {
            IQuerySnapshot<SystemAlertProxy> snapshot = await CrossFirebaseFirestore.Current
                .GetCollection("system_alerts")
                .WhereEqualsTo("type", "DriverIdle")
                .GetDocumentsAsync<SystemAlertProxy>();

            List<AlertItem> idleAlerts = snapshot.Documents
                .Where(doc => doc.Data != null && !doc.Data.IsRead)
                .OrderByDescending(doc => doc.Data!.Timestamp)
                .Select(doc => new AlertItem
                {
                    AlertId = doc.Reference.Id,
                    Type = AlertType.DriverIdle,
                    DriverName = $"{doc.Data!.DriverName} · {doc.Data.UnitLabel}",
                    Subtitle = doc.Data.Message,
                    Timestamp = doc.Data.Timestamp.ToLocalTime().ToString("h:mm tt"),
                })
                .ToList();

            if (idleAlerts.Count == 0) return;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                foreach (AlertItem item in idleAlerts)
                {
                    Alerts.Add(item);
                }
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Idle Alert Load Error: {ex.Message}");
        }
    }

    private static async Task MarkAlertReadAsync(string alertId)
    {
        try
        {
            await CrossFirebaseFirestore.Current
                .GetCollection("system_alerts")
                .GetDocument(alertId)
                .UpdateDataAsync(new Dictionary<object, object> { { "isRead", true } });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Mark Alert Read Error: {ex.Message}");
        }
    }
}

public enum AlertType
{
    Sos,
    FuelDiscrepancy,
    ShiftApproval,
    DriverIdle
}

public class AlertItem
{
    /// <summary>Firestore document id for a real (system_alerts-backed) alert; empty for the
    /// mock SOS/Fuel/ShiftApproval cards, which don't persist a dismissal anywhere.</summary>
    public string AlertId { get; set; } = string.Empty;
    public AlertType Type { get; set; }
    public string DriverName { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public string Timestamp { get; set; } = string.Empty;
    public string? FailedItem { get; set; }
    public string? Priority { get; set; }
    public string? DriverDescription { get; set; }

    public bool IsSos => Type == AlertType.Sos;
    public bool IsFuelDiscrepancy => Type == AlertType.FuelDiscrepancy;
    public bool IsShiftApproval => Type == AlertType.ShiftApproval;
    public bool IsDriverIdle => Type == AlertType.DriverIdle;
}

// Local proxy class using mobile-specific Plugin.Firebase attributes - same convention as
// ChatMessageProxy in ChatService.cs.
public class SystemAlertProxy
{
    [FirestoreProperty("type")]
    public string Type { get; set; } = string.Empty;

    [FirestoreProperty("driverId")]
    public string DriverId { get; set; } = string.Empty;

    [FirestoreProperty("driverName")]
    public string DriverName { get; set; } = string.Empty;

    [FirestoreProperty("taxiId")]
    public string TaxiId { get; set; } = string.Empty;

    [FirestoreProperty("unitLabel")]
    public string UnitLabel { get; set; } = string.Empty;

    [FirestoreProperty("shiftId")]
    public string ShiftId { get; set; } = string.Empty;

    [FirestoreProperty("message")]
    public string Message { get; set; } = string.Empty;

    [FirestoreProperty("timestamp")]
    public DateTime Timestamp { get; set; }

    [FirestoreProperty("isRead")]
    public bool IsRead { get; set; }
}
