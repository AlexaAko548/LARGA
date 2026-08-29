namespace LARGA.MobileApp.Views.Manager;

public partial class AlertCenterPage : ContentPage
{
    public AlertCenterPage()
    {
        InitializeComponent();
    }

    private void OnDismissSosClicked(object sender, EventArgs e)
    {
        // TODO: mark SOS alert as resolved in Firestore, remove card
    }

    private async void OnViewLocationClicked(object sender, EventArgs e)
    {
        // TODO: navigate to fleet map centered on this driver's location
    }

    private async void OnCallDriverClicked(object sender, EventArgs e)
    {
        // TODO: trigger phone call to driver's registered number
    }

    private async void OnMessageDriverClicked(object sender, EventArgs e)
    {
        // TODO: open chat/message thread with driver
    }

    private void OnDismissFuelAlertClicked(object sender, EventArgs e)
    {
        // TODO: mark fuel discrepancy alert as dismissed in Firestore, remove card
    }

    private void OnDismissApprovalClicked(object sender, EventArgs e)
    {
        // TODO: dismiss without action (edge case, may not be allowed per business rules)
    }

    private async void OnViewEvidenceClicked(object sender, EventArgs e)
    {
        // TODO: open photo viewer showing the attached damage evidence photo
    }

    private void OnApproveClicked(object sender, EventArgs e)
    {
        // TODO: update ShiftLog/HandoverChecklist status to approved in Firestore, remove card
    }

    private void OnDenyClicked(object sender, EventArgs e)
    {
        // TODO: update ShiftLog status to denied, create/link MaintenanceRecord, notify driver, remove card
    }
}