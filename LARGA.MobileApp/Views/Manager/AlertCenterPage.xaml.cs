using LARGA.Shared.Models;
using System.Collections.ObjectModel;

namespace LARGA.MobileApp.Views.Manager;

public class ShiftApprovalAlert
{
    public string ChecklistId { get; set; } = string.Empty;
    public string DriverName { get; set; } = string.Empty;
    public string UnitLabel { get; set; } = string.Empty;
    public string FailedItemLabel { get; set; } = string.Empty;
    public string PriorityLabel { get; set; } = string.Empty;
    public string DriverDescription { get; set; } = string.Empty;
}

public partial class AlertCenterPage : ContentPage
{
    public ObservableCollection<ShiftApprovalAlert> Alerts { get; set; } = new();

    public AlertCenterPage()
    {
        InitializeComponent();
        AlertsCollectionView.ItemsSource = Alerts;

        // TODO: replace with real data pulled from Firebase
        LoadMockData();
    }

    private void LoadMockData()
    {
        Alerts.Add(new ShiftApprovalAlert
        {
            ChecklistId = "sample-checklist-id",
            DriverName = "Juan Dela Cruz · Unit 02",
            UnitLabel = "10:45 AM",
            FailedItemLabel = "FAILED ITEM: Tire Condition",
            PriorityLabel = "PRIORITY: High",
            DriverDescription = "Right passenger side rear tire is completely flat. Found a nail in it during walk-around."
        });

        ActiveNotificationsLabel.Text = $"{Alerts.Count} active notifications";
    }

    private void OnApproveClicked(object sender, EventArgs e)
    {
        if (sender is Button button && button.CommandParameter is string checklistId)
        {
            // TODO: update ShiftLog/HandoverChecklist status to approved in Firebase
            // then remove from Alerts collection
        }
    }

    private void OnDenyClicked(object sender, EventArgs e)
    {
        if (sender is Button button && button.CommandParameter is string checklistId)
        {
            // TODO: update ShiftLog status, create/link MaintenanceRecord,
            // notify driver the shift is denied, then remove from Alerts collection
        }
    }
}