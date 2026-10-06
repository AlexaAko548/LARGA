using Plugin.Firebase.CloudMessaging;
using Microsoft.Maui.Controls;
using Microsoft.Maui.ApplicationModel;
using System.Threading.Tasks;


namespace LARGA.MobileApp;

public partial class App : Application
{
	private readonly LARGA.MobileApp.Services.EmergencyCountdownCoordinator _countdown;
	private Page? _countdownPage;
	private bool _syncingCountdown;

	public App(LARGA.MobileApp.Services.EmergencyCountdownCoordinator countdown)
	{
		InitializeComponent();

		// LAR-86/87: show the cancel pop-up whenever an automated SOS countdown starts, and close
		// it when the countdown ends (sent or cancelled). Changed can fire from any thread.
		_countdown = countdown;
		_countdown.Changed += (_, _) => MainThread.BeginInvokeOnMainThread(() => _ = SyncCountdownPageAsync());

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
		var window = new Window(new AppShell());
		// Brought forward by the countdown's full-screen notification (screen off / app in the
		// background): the countdown may have started before the page could be shown.
		window.Resumed += (_, _) => _ = SyncCountdownPageAsync();
		return window;
	}

	private async Task SyncCountdownPageAsync()
	{
		if (_syncingCountdown) return;
		_syncingCountdown = true;
		try
		{
			// Shell can still be initializing when the full-screen intent cold-starts the UI.
			for (int i = 0; i < 20 && Shell.Current == null; i++)
			{
				await Task.Delay(100);
			}

			INavigation? navigation = Shell.Current?.Navigation;
			if (navigation == null) return;

			if (_countdown.Active != null && _countdownPage == null)
			{
				_countdownPage = new Views.Shared.EmergencyCountdownPage(_countdown);
				await navigation.PushModalAsync(_countdownPage, false);
			}
			else if (_countdown.Active == null && _countdownPage != null)
			{
				Page page = _countdownPage;
				_countdownPage = null;
				if (navigation.ModalStack.Count > 0 && navigation.ModalStack[^1] == page)
				{
					await navigation.PopModalAsync(false);
				}
			}
		}
		catch (Exception ex)
		{
			// The countdown notification (with its own Cancel action) is still showing.
			System.Diagnostics.Debug.WriteLine($"Emergency countdown pop-up failed: {ex.Message}");
			return;
		}
		finally
		{
			_syncingCountdown = false;
		}

		// A Changed that arrived while this was running may have flipped the state again.
		bool shown = _countdownPage != null;
		if (shown != (_countdown.Active != null))
		{
			await SyncCountdownPageAsync();
		}
	}
}