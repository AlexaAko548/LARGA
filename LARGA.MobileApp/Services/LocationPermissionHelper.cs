using System;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;

namespace LARGA.MobileApp.Services;

/// <summary>
/// Asks for location permission on the UI thread - MAUI can only show the prompt from there.
/// The clock-in screens call it before starting GPS telemetry (LAR-77), whose background loop
/// only checks the permission. Never throws: tracking simply writes nothing without it.
/// </summary>
public static class LocationPermissionHelper
{
    public static async Task<bool> EnsureWhenInUseAsync()
    {
        try
        {
            return await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                PermissionStatus status = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
                if (status != PermissionStatus.Granted)
                {
                    status = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
                }
                return status == PermissionStatus.Granted;
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Location permission request failed: {ex.Message}");
            return false;
        }
    }
}
