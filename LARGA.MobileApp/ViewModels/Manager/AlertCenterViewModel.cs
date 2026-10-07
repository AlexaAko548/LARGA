using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using LARGA.MobileApp.Services;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.ApplicationModel.Communication;
using Microsoft.Maui.Controls;
using Plugin.Firebase.Firestore;

namespace LARGA.MobileApp.ViewModels.Manager;

public class AlertCenterViewModel : BindableObject
{
    // Four independent live feeds merged into one list for the UI. Each Firestore
    // snapshot listener owns its slice and rebuilds it in full on every change.
    private readonly List<AlertItem> _sosItems = new();
    private readonly List<AlertItem> _fuelItems = new();
    private readonly List<AlertItem> _defectItems = new();
    private readonly List<AlertItem> _idleItems = new();
    private readonly List<AlertItem> _lowStockItems = new();

    private readonly Dictionary<string, DriverLookup> _driverCache = new();
    private readonly Dictionary<string, ShiftProxy> _shiftCache = new();

    private bool _isListening;
    private IDisposable? _sosListener;
    private IDisposable? _fuelListener;
    private IDisposable? _defectListener;
    private IDisposable? _idleListener;
    private IDisposable? _lowStockListener;

    // Cards the manager closed with X this session. X never makes a decision on the record (Resolve,
    // Approve and Deny do), so without this the live listener would keep showing the card.
    private readonly HashSet<string> _hiddenIds = new();

    public ObservableCollection<AlertItem> Alerts { get; } = new();

    // Same alerts as Alerts, newest first, sectioned by date for the CollectionView.
    public ObservableCollection<AlertGroup> AlertGroups { get; } = new();

    public ICommand LoadAlertsCommand { get; }
    public ICommand DismissAlertCommand { get; }
    public ICommand ApproveShiftCommand { get; }
    public ICommand DenyShiftCommand { get; }
    public ICommand ResolveFuelCommand { get; }
    public ICommand CallDriverCommand { get; }
    public ICommand MessageDriverCommand { get; }
    public ICommand ViewLocationCommand { get; }

    public AlertCenterViewModel()
    {
        // Idempotent - OnAppearing calls this every time the tab is revisited, but the
        // listeners themselves should only ever be started once for this VM's lifetime.
        LoadAlertsCommand = new Command(StartListening);

        DismissAlertCommand = new Command<AlertItem>(async (alert) => await DismissAsync(alert));
        ApproveShiftCommand = new Command<AlertItem>(async (alert) => await ResolveDefectAsync(alert, "Dismissed"));
        DenyShiftCommand = new Command<AlertItem>(async (alert) => await ResolveDefectAsync(alert, "InProgress"));
        ResolveFuelCommand = new Command<AlertItem>(async (alert) => await ResolveFuelFlagAsync(alert));

        CallDriverCommand = new Command<AlertItem>((alert) =>
        {
            if (alert == null || string.IsNullOrWhiteSpace(alert.PhoneNumber)) return;
            if (PhoneDialer.Default.IsSupported)
            {
                PhoneDialer.Default.Open(alert.PhoneNumber);
            }
        });

        MessageDriverCommand = new Command<AlertItem>(async (alert) =>
        {
            if (alert == null || string.IsNullOrWhiteSpace(alert.DriverId)) return;

            // Same route/parameters ChatsViewModel.OnOpenChat uses to open a driver's thread -
            // dictionary parameters keep DriverName intact if it has spaces.
            await Shell.Current.GoToAsync("ChatsDetailPage", new Dictionary<string, object>
            {
                { "DriverId", alert.DriverId },
                { "DriverName", alert.DriverName }
            });
        });

        ViewLocationCommand = new Command<AlertItem>(async (alert) =>
        {
            if (alert == null || alert.Latitude == null || alert.Longitude == null) return;
            try
            {
                // Redirect into the app's own Live Fleet map (Map tab) centered on this
                // driver, rather than handing off to an external maps app - a manager
                // responding to an SOS wants the same map they already use for the fleet,
                // not a separate app switch.
                MapFocusRequest.Request(alert.DriverId, alert.Latitude.Value, alert.Longitude.Value);
                await Shell.Current.GoToAsync("//manager-dashboard/home");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"View Location Error: {ex.Message}");
            }
        });
    }

    private void StartListening()
    {
        if (_isListening) return;
        _isListening = true;

        _sosListener = CrossFirebaseFirestore.Current
            .GetCollection("emergency_alerts")
            .WhereEqualsTo("isResolved", false)
            .AddSnapshotListener<EmergencyAlertProxy>(async snapshot =>
            {
                var items = new List<AlertItem>();
                foreach (var doc in snapshot.Documents)
                {
                    if (doc.Data == null) continue;
                    var shift = await GetShiftAsync(doc.Data.ShiftId);
                    var driver = shift != null ? await GetDriverAsync(shift.DriverId) : null;

                    items.Add(new AlertItem
                    {
                        Id = doc.Reference.Id,
                        Type = AlertType.Sos,
                        DriverId = shift?.DriverId,
                        DriverName = BuildDriverLabel(driver, shift),
                        Subtitle = $"Location: {doc.Data.Latitude:F5}, {doc.Data.Longitude:F5}",
                        SortTime = FirestoreDateTimeFix.Apply(doc.Data.Timestamp),
                        Timestamp = FormatAlertTime(FirestoreDateTimeFix.Apply(doc.Data.Timestamp)),
                        Latitude = doc.Data.Latitude,
                        Longitude = doc.Data.Longitude,
                        PhoneNumber = driver?.PhoneNumber?.ToString()
                    });
                }

                _sosItems.Clear();
                _sosItems.AddRange(items);
                MainThread.BeginInvokeOnMainThread(RefreshCombinedAlerts);
            });

        _fuelListener = CrossFirebaseFirestore.Current
            .GetCollection("fuel_logs")
            .WhereEqualsTo("verificationStatus", "Flagged")
            .AddSnapshotListener<FuelLogProxy>(async snapshot =>
            {
                var items = new List<AlertItem>();
                foreach (var doc in snapshot.Documents)
                {
                    if (doc.Data == null) continue;
                    var shift = await GetShiftAsync(doc.Data.ShiftId);
                    var driver = shift != null ? await GetDriverAsync(shift.DriverId) : null;

                    var subtitle = !string.IsNullOrWhiteSpace(doc.Data.FuelLogDetails)
                        ? doc.Data.FuelLogDetails
                        : $"Fuel receipt flagged for review ({doc.Data.LitersRefueled}L, ₱{doc.Data.FuelCost}).";

                    items.Add(new AlertItem
                    {
                        Id = doc.Reference.Id,
                        Type = AlertType.FuelDiscrepancy,
                        DriverName = BuildDriverLabel(driver, shift),
                        Subtitle = subtitle,
                        SortTime = doc.Data.ReceiptTimestamp != null
                            ? FirestoreDateTimeFix.Apply(doc.Data.ReceiptTimestamp.Value.UtcDateTime)
                            : DateTime.MinValue,
                        Timestamp = doc.Data.ReceiptTimestamp != null
                            ? FormatAlertTime(FirestoreDateTimeFix.Apply(doc.Data.ReceiptTimestamp.Value.UtcDateTime))
                            : "--",
                        PhoneNumber = driver?.PhoneNumber?.ToString()
                    });
                }

                _fuelItems.Clear();
                _fuelItems.AddRange(items);
                MainThread.BeginInvokeOnMainThread(RefreshCombinedAlerts);
            });

        _defectListener = CrossFirebaseFirestore.Current
            .GetCollection("maintenance_logs")
            .WhereEqualsTo("status", "Reported")
            .AddSnapshotListener<MaintenanceLogProxy>(async snapshot =>
            {
                var items = new List<AlertItem>();
                foreach (var doc in snapshot.Documents)
                {
                    if (doc.Data == null) continue;
                    var driver = await GetDriverAsync(doc.Data.ReportedByDriverId ?? string.Empty);

                    items.Add(new AlertItem
                    {
                        Id = doc.Reference.Id,
                        Type = AlertType.ShiftApproval,
                        TaxiId = doc.Data.TaxiId,
                        DriverName = BuildDriverLabel(driver, null, doc.Data.TaxiId),
                        SortTime = FirestoreDateTimeFix.Apply(doc.Data.DateLogged),
                        Timestamp = FormatAlertTime(FirestoreDateTimeFix.Apply(doc.Data.DateLogged)),
                        FailedItem = doc.Data.IssueTitle,
                        Priority = doc.Data.PriorityLevel,
                        DriverDescription = doc.Data.IssueDescription,
                        PhoneNumber = driver?.PhoneNumber?.ToString()
                    });
                }

                _defectItems.Clear();
                _defectItems.AddRange(items);
                MainThread.BeginInvokeOnMainThread(RefreshCombinedAlerts);
            });

        // system_alerts feeds: one listener per alert type, sharing the same query shape.
        _idleListener = ListenToSystemAlerts(AlertType.DriverIdle, _idleItems);
        _lowStockListener = ListenToSystemAlerts(AlertType.LowStock, _lowStockItems);
    }

    // Loads real, unread system_alerts of one type. Single equality filter (type == <alertType>)
    // only - no composite index needed - with the isRead filter and timestamp ordering done
    // client-side. The enum name is the stored `type` string, so DriverIdle and LowStock match
    // what AlertService and IdleAlertMonitorService write.
    private IDisposable ListenToSystemAlerts(AlertType alertType, List<AlertItem> bucket)
    {
        return CrossFirebaseFirestore.Current
            .GetCollection("system_alerts")
            .WhereEqualsTo("type", alertType.ToString())
            .AddSnapshotListener<SystemAlertProxy>(snapshot =>
            {
                var items = snapshot.Documents
                    .Where(doc => doc.Data != null && !doc.Data.IsRead)
                    .OrderByDescending(doc => doc.Data!.Timestamp)
                    .Select(doc => new AlertItem
                    {
                        Id = doc.Reference.Id,
                        Type = alertType,
                        DriverId = doc.Data!.DriverId,
                        TaxiId = doc.Data.TaxiId,
                        DriverName = alertType == AlertType.DriverIdle
                            ? $"{doc.Data.DriverName} · {doc.Data.UnitLabel}"
                            : "Spare Parts Inventory",
                        Subtitle = doc.Data.Message,
                        SortTime = FirestoreDateTimeFix.Apply(doc.Data.Timestamp),
                        Timestamp = FormatAlertTime(FirestoreDateTimeFix.Apply(doc.Data.Timestamp))
                    })
                    .ToList();

                bucket.Clear();
                bucket.AddRange(items);
                MainThread.BeginInvokeOnMainThread(RefreshCombinedAlerts);
            });
    }

    private void RefreshCombinedAlerts()
    {
        // Newest first across all alert types. GroupBy keeps that order, so each date section
        // starts with its newest alert and the newest section comes first.
        List<AlertItem> sorted = _sosItems.Concat(_fuelItems).Concat(_defectItems).Concat(_idleItems).Concat(_lowStockItems)
            .Where(item => !_hiddenIds.Contains(item.Id))
            .OrderByDescending(item => item.SortTime)
            .ToList();

        Alerts.Clear();
        foreach (var item in sorted)
        {
            Alerts.Add(item);
        }

        AlertGroups.Clear();
        foreach (var group in sorted.GroupBy(item => SectionTitle(item.SortTime)))
        {
            AlertGroups.Add(new AlertGroup(group.Key, group));
        }
    }

    // Time-only for alerts from today, date + time for older ones, so a card from last week
    // doesn't look like it happened this morning.
    private static string FormatAlertTime(DateTime utc)
    {
        if (utc == DateTime.MinValue) return "--";
        DateTime local = utc.ToLocalTime();
        return local.Date == DateTime.Now.Date
            ? local.ToString("h:mm tt")
            : local.ToString("MMM d, yyyy h:mm tt");
    }

    private static string SectionTitle(DateTime utc)
    {
        if (utc == DateTime.MinValue) return "Undated";
        DateTime localDate = utc.ToLocalTime().Date;
        if (localDate == DateTime.Now.Date) return "Today";
        if (localDate == DateTime.Now.Date.AddDays(-1)) return "Yesterday";
        return localDate.ToString("MMMM d, yyyy");
    }

    private async Task<DriverLookup?> GetDriverAsync(string driverId)
    {
        if (string.IsNullOrWhiteSpace(driverId)) return null;
        if (_driverCache.TryGetValue(driverId, out var cached)) return cached;

        var doc = await CrossFirebaseFirestore.Current
            .GetCollection("users")
            .GetDocument(driverId)
            .GetDocumentSnapshotAsync<DriverLookup>();
        if (doc?.Data == null) return null;

        _driverCache[driverId] = doc.Data;
        return doc.Data;
    }

    private async Task<ShiftProxy?> GetShiftAsync(string shiftId)
    {
        if (string.IsNullOrWhiteSpace(shiftId)) return null;
        if (_shiftCache.TryGetValue(shiftId, out var cached)) return cached;

        var doc = await CrossFirebaseFirestore.Current
            .GetCollection("shifts")
            .GetDocument(shiftId)
            .GetDocumentSnapshotAsync<ShiftProxy>();
        if (doc?.Data == null) return null;

        _shiftCache[shiftId] = doc.Data;
        return doc.Data;
    }

    private static string BuildDriverLabel(DriverLookup? driver, ShiftProxy? shift, string? taxiIdOverride = null)
    {
        var name = string.IsNullOrWhiteSpace(driver?.FullName) ? "Unknown Driver" : driver!.FullName;
        var taxiId = taxiIdOverride ?? shift?.TaxiId;
        return string.IsNullOrWhiteSpace(taxiId) ? name : $"{name} · {taxiId}";
    }

    private async Task DismissAsync(AlertItem? alert)
    {
        if (alert == null) return;

        try
        {
            switch (alert.Type)
            {
                case AlertType.Sos:
                    // Same writes as the web's SOS Dispatch (SosDispatchService.SetResolvedAsync):
                    // the resolved time for its Resolved list, and the matching bell alert read.
                    await CrossFirebaseFirestore.Current
                        .GetCollection("emergency_alerts")
                        .GetDocument(alert.Id)
                        .UpdateDataAsync(new Dictionary<object, object>
                        {
                            ["isResolved"] = true,
                            ["resolvedAt"] = DateTime.UtcNow,
                        });
                    await MarkSosBellReadAsync(alert.Id);
                    await AuditLogWriter.WriteAsync("SosResolved",
                        $"Resolved SOS alert for {alert.TaxiId} ({alert.DriverName}).");
                    break;

                case AlertType.FuelDiscrepancy:
                case AlertType.ShiftApproval:
                    // No status write here - Resolve (fuel) and Approve/Deny (defect) are the real
                    // decisions; X just hides the card for this session.
                    _hiddenIds.Add(alert.Id);
                    RefreshCombinedAlerts();
                    break;

                case AlertType.DriverIdle:
                case AlertType.LowStock:
                    await MarkAlertReadAsync(alert.Id);
                    break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Dismiss Alert Error: {ex.Message}");
        }
    }

    private async Task ResolveDefectAsync(AlertItem? alert, string newStatus)
    {
        if (alert == null || alert.Type != AlertType.ShiftApproval) return;

        try
        {
            await CrossFirebaseFirestore.Current
                .GetCollection("maintenance_logs")
                .GetDocument(alert.Id)
                .UpdateDataAsync(new Dictionary<object, object> { ["status"] = newStatus });

            // "Deny & Send to Garage" means the taxi itself is now unavailable.
            if (newStatus == "InProgress" && !string.IsNullOrWhiteSpace(alert.TaxiId))
            {
                await CrossFirebaseFirestore.Current
                    .GetCollection("taxis")
                    .GetDocument(alert.TaxiId)
                    .UpdateDataAsync(new Dictionary<object, object> { ["status"] = LARGA.SharedCore.TaxiStatusRules.UnderMaintenance });
            }

            if (newStatus == "InProgress")
            {
                await AuditLogWriter.WriteAsync("VehicleDefectDeniedUnitMaintenance",
                    $"Vehicle defect report denied for {alert.TaxiId} ({alert.DriverName}): {alert.FailedItem}. Unit put under maintenance.");
            }
            else
            {
                await AuditLogWriter.WriteAsync("VehicleDefectApproved",
                    $"Vehicle defect report approved for {alert.TaxiId} ({alert.DriverName}): {alert.FailedItem}.");
            }

            if (newStatus == "InProgress")
            {
                await Shell.Current.DisplayAlert("Sent to Garage", $"{alert.DriverName}'s report has been sent to the garage for a work order. {alert.TaxiId} is now marked under maintenance.", "OK");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Resolve Defect Error: {ex.Message}");
            await Shell.Current.DisplayAlert("Error", "Could not send this report to the garage. Please try again.", "OK");
        }
    }

    /// <summary>The bell entry ManagerWeb raised for this SOS ({alertId}_SOS), if any.</summary>
    private static async Task MarkSosBellReadAsync(string alertId)
    {
        try
        {
            var bell = CrossFirebaseFirestore.Current.GetCollection("system_alerts").GetDocument($"{alertId}_SOS");
            var snapshot = await bell.GetDocumentSnapshotAsync<BellProxy>();
            if (snapshot?.Data != null)
            {
                await bell.UpdateDataAsync(new Dictionary<object, object> { ["isRead"] = true });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SOS bell update error: {ex.Message}");
        }
    }

    /// <summary>
    /// Resolves a flagged fuel log the same way as the web's Fuel Verification
    /// (FuelVerificationService.ResolveFlagAsync): a resolution note is required, it's added to
    /// fuelLogDetails as "Resolved: ...", and the log moves to Verified.
    /// </summary>
    private async Task ResolveFuelFlagAsync(AlertItem? alert)
    {
        if (alert == null || alert.Type != AlertType.FuelDiscrepancy) return;

        string? note = await Shell.Current.DisplayPromptAsync(
            "Resolve fuel flag", "How was this discrepancy resolved?", "Resolve", "Cancel",
            placeholder: "e.g. Receipt re-checked with the driver", maxLength: 500);
        if (note is null) return; // cancelled
        note = note.Trim();
        if (note.Length == 0)
        {
            await Shell.Current.DisplayAlert("Note required", "Enter a short note on how this was resolved.", "OK");
            return;
        }

        try
        {
            var document = CrossFirebaseFirestore.Current.GetCollection("fuel_logs").GetDocument(alert.Id);
            var snapshot = await document.GetDocumentSnapshotAsync<FuelLogProxy>();
            string existing = snapshot?.Data?.FuelLogDetails ?? string.Empty;
            string combined = string.IsNullOrWhiteSpace(existing) ? $"Resolved: {note}" : $"{existing}\n\nResolved: {note}";

            await document.UpdateDataAsync(new Dictionary<object, object>
            {
                ["verificationStatus"] = "Verified",
                ["fuelLogDetails"] = combined,
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Resolve fuel flag error: {ex.Message}");
            await Shell.Current.DisplayAlert("Error", "Could not resolve this flag. Please try again.", "OK");
        }
    }

    private class BellProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("isRead")]
        public bool IsRead { get; set; }
    }

    private class DriverLookup
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("fullName")]
        public string FullName { get; set; } = string.Empty;

        [Plugin.Firebase.Firestore.FirestoreProperty("phoneNumber")]
        public object? PhoneNumber { get; set; }
    }

    private class ShiftProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("driverId")]
        public string DriverId { get; set; } = string.Empty;

        [Plugin.Firebase.Firestore.FirestoreProperty("taxiId")]
        public string TaxiId { get; set; } = string.Empty;
    }

    private class EmergencyAlertProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("shiftId")]
        public string ShiftId { get; set; } = string.Empty;

        [Plugin.Firebase.Firestore.FirestoreProperty("latitude")]
        public double Latitude { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("longitude")]
        public double Longitude { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("isResolved")]
        public bool IsResolved { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("timestamp")]
        public DateTime Timestamp { get; set; }
    }

    private class FuelLogProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("shiftId")]
        public string ShiftId { get; set; } = string.Empty;

        [Plugin.Firebase.Firestore.FirestoreProperty("litersRefueled")]
        public double LitersRefueled { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("fuelCost")]
        public double FuelCost { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("fuelLogDetails")]
        public string? FuelLogDetails { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("receiptTimestamp")]
        public DateTimeOffset? ReceiptTimestamp { get; set; }
    }

    private class MaintenanceLogProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("issueTitle")]
        public string IssueTitle { get; set; } = string.Empty;

        [Plugin.Firebase.Firestore.FirestoreProperty("issueDescription")]
        public string IssueDescription { get; set; } = string.Empty;

        [Plugin.Firebase.Firestore.FirestoreProperty("priorityLevel")]
        public string PriorityLevel { get; set; } = string.Empty;

        [Plugin.Firebase.Firestore.FirestoreProperty("dateLogged")]
        public DateTime DateLogged { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("reportedByDriverId")]
        public string? ReportedByDriverId { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("taxiId")]
        public string? TaxiId { get; set; }
    }

    private class SystemAlertProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("type")]
        public string Type { get; set; } = string.Empty;

        [Plugin.Firebase.Firestore.FirestoreProperty("driverId")]
        public string DriverId { get; set; } = string.Empty;

        [Plugin.Firebase.Firestore.FirestoreProperty("driverName")]
        public string DriverName { get; set; } = string.Empty;

        [Plugin.Firebase.Firestore.FirestoreProperty("taxiId")]
        public string TaxiId { get; set; } = string.Empty;

        [Plugin.Firebase.Firestore.FirestoreProperty("unitLabel")]
        public string UnitLabel { get; set; } = string.Empty;

        [Plugin.Firebase.Firestore.FirestoreProperty("shiftId")]
        public string ShiftId { get; set; } = string.Empty;

        [Plugin.Firebase.Firestore.FirestoreProperty("message")]
        public string Message { get; set; } = string.Empty;

        [Plugin.Firebase.Firestore.FirestoreProperty("timestamp")]
        public DateTime Timestamp { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("isRead")]
        public bool IsRead { get; set; }
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

// One date section in the Alert Center list ("Today", "Yesterday", "October 3, 2026").
public class AlertGroup : ObservableCollection<AlertItem>
{
    public AlertGroup(string title, IEnumerable<AlertItem> items) : base(items)
    {
        Title = title;
    }

    public string Title { get; }
}

public enum AlertType
{
    Sos,
    FuelDiscrepancy,
    ShiftApproval,
    DriverIdle,
    LowStock
}

public class AlertItem
{
    public string Id { get; set; } = string.Empty;
    public AlertType Type { get; set; }
    public string? DriverId { get; set; }
    public string? TaxiId { get; set; }
    public string DriverName { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public string Timestamp { get; set; } = string.Empty;
    // Real UTC moment the alert happened. Drives newest-first ordering and date sections;
    // Timestamp is display text only.
    public DateTime SortTime { get; set; }
    public string? FailedItem { get; set; }
    public string? Priority { get; set; }
    public string? DriverDescription { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? PhoneNumber { get; set; }

    public bool IsSos => Type == AlertType.Sos;
    public bool IsFuelDiscrepancy => Type == AlertType.FuelDiscrepancy;
    public bool IsShiftApproval => Type == AlertType.ShiftApproval;
    public bool IsDriverIdle => Type == AlertType.DriverIdle;
}