using System.Collections.ObjectModel;
using System.Windows.Input;
using Microsoft.Maui.Controls;

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
        DismissAlertCommand = new Command<AlertItem>((alert) =>
        {
            if (alert != null) Alerts.Remove(alert);
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
}

public enum AlertType
{
    Sos,
    FuelDiscrepancy,
    ShiftApproval
}

public class AlertItem
{
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
}