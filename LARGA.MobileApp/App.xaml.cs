using Plugin.Firebase.CloudMessaging;
using Microsoft.Maui.Controls;
using Microsoft.Maui.ApplicationModel;
using System.Threading.Tasks;


namespace LARGA.MobileApp;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
		
		// 1. Handle notification tapped/opened by the driver
        CrossFirebaseCloudMessaging.Current.NotificationTapped += async (sender, e) =>
        {
            if (e.Notification?.Data != null)
            {
                // Check payload key sent by the backend
                if (e.Notification.Data.TryGetValue("type", out var type) && type?.ToString() == "pre_shift_reminder")
                {
                    await MainThread.InvokeOnMainThreadAsync(async () =>
                    {
                        // Wait briefly if the shell is still initializing during cold start
                        while (Shell.Current == null)
                        {
                            await Task.Delay(100);
                        }

                        // Route directly to the pre-shift inspection workflow
                        await Shell.Current.GoToAsync("//PreShiftStep1Page");
                    });
                }
            }
        };

        // 2. Handle foreground notifications while the app is actively open
        CrossFirebaseCloudMessaging.Current.NotificationReceived += (sender, e) =>
        {
            System.Diagnostics.Debug.WriteLine($"FCM Received in foreground: {e.Notification?.Body}");
        };
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new AppShell());
	}
}