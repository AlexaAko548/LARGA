# LARGA load test on 5 emulators

Scripts for running the 5 test drivers of the load test (Perf - Load Test (5+1)) as Android emulators on one Windows laptop.
Run them from PowerShell in this folder. If PowerShell blocks scripts, run once: `Set-ExecutionPolicy -Scope CurrentUser RemoteSigned`

| Script | What it does |
| --- | --- |
| `setup-emulators.ps1 -Create` | First time only: creates the AVDs LARGA_A ... LARGA_E |
| `setup-emulators.ps1 -Apk <path>` | Starts all 5 and installs the LARGA APK on each |
| `drive-routes.ps1 -ParkOnly` | Puts all 5 at the garage pin and keeps them there |
| `drive-routes.ps1 -Minutes 30` | Drives all 5 on Cebu City routes at once; each returns to the garage after about 30 min |
| `tap-all.ps1 -Text "Start Shift"` | Taps Confirm & Start Shift on all 5 at once (and "End Shift" for clock-out) |

Emulator serials: emulator-5554 = Driver A, 5556 = B, 5558 = C, 5560 = D, 5562 = E.
Optional road-accurate routes: save GPX files as `routes\A.gpx` ... `routes\E.gpx` (e.g. drawn on gpx.studio).
