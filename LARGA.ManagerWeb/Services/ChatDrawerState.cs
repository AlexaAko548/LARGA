namespace LARGA.ManagerWeb.Services;

/// <summary>
/// Which conversation the Messages drawer (Components/Shared/MessagesButton.razor) is
/// showing, shared per browser session so a page can open it straight into a driver's chat -
/// SOS Dispatch's Message button does. DriverId null = the conversation list.
/// </summary>
public class ChatDrawerState
{
    public bool IsOpen { get; private set; }
    public string? DriverId { get; private set; }
    public string DriverName { get; private set; } = string.Empty;

    /// <summary>Second line under the driver's name, e.g. "TAXI_002 · SOS 2:48 PM".</summary>
    public string? Subtitle { get; private set; }

    /// <summary>Opened from an SOS - the chat offers the emergency quick replies.</summary>
    public bool IsEmergency { get; private set; }

    public event Action? Changed;

    public void OpenList()
    {
        IsOpen = true;
        ShowList();
    }

    public void OpenChat(string driverId, string driverName, string? subtitle = null, bool isEmergency = false)
    {
        IsOpen = true;
        DriverId = driverId;
        DriverName = driverName;
        Subtitle = subtitle;
        IsEmergency = isEmergency;
        Changed?.Invoke();
    }

    /// <summary>The back arrow: from a chat to the conversation list.</summary>
    public void ShowList()
    {
        DriverId = null;
        DriverName = string.Empty;
        Subtitle = null;
        IsEmergency = false;
        Changed?.Invoke();
    }

    public void Close()
    {
        IsOpen = false;
        ShowList();
    }
}
