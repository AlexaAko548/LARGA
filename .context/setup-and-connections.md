# LARGA — Setup & Connections

Covers all live wiring: DI registrations, Firebase configuration, cross-project references, SDK tiers, and navigation routing. Companion to `architecture.md` (big picture) and `database-schema.md` (Firestore collections).

---

## 1. Solution Projects & References

| Project | Type | Targets |
|---|---|---|
| `LARGA.MobileApp` | .NET MAUI | net9.0-android (primary), iOS, Windows |
| `LARGA.SharedCore` | Class Library | net9.0 |
| `LARGA.Shared.Models` | Class Library | net9.0 |
| `LARGA.ManagerWeb` | Blazor Server | net9.0 |
| `LARGA.SeedTool` | Console App | net9.0 |
| `LARGA.MobileApp.Tests` | xUnit | net9.0 |

**Project reference graph:**
```
LARGA.MobileApp  ──►  LARGA.SharedCore  ──►  LARGA.Shared.Models
LARGA.ManagerWeb ──►  LARGA.SharedCore
LARGA.SeedTool   ──►  LARGA.Shared.Models
LARGA.MobileApp.Tests  ──►  LARGA.Shared.Models
                       ──►  (source-links QuickLedgerCalculator.cs, BoundaryPaymentRollup.cs directly)
```

---

## 2. Firebase Project

- **Project ID:** `larga-blmtaxi`
- **Two SDK tiers run against the same project simultaneously:**

| Tier | SDK | Used By | Auth |
|---|---|---|---|
| Client SDK | Plugin.Firebase 4.x | LARGA.MobileApp | `google-services.json` |
| Admin SDK | Google.Cloud.Firestore 4.4 + FirebaseAdmin 3.6 | LARGA.ManagerWeb, LARGA.SeedTool | Service account JSON (gitignored) |

---

## 3. Mobile Firebase Setup (Plugin.Firebase)

### Android initialization — `MauiProgram.RegisterFirebaseServices()`

```csharp
CrossFirebase.Initialize(activity, new CrossFirebaseSettings(
    IsAuthEnabled: true,
    IsCloudFirestoreEnabled: true,
    IsStorageEnabled: true,
    IsCloudMessagingEnabled: true
));
```

**Config file:** `LARGA.MobileApp/Platforms/Android/google-services.json`
(included as `<GoogleServicesJson>` in the `.csproj`)

### iOS initialization — `AppDelegate.FinishedLaunching`

```csharp
CrossFirebase.Initialize();
```

### Access patterns (mobile services)

```csharp
CrossFirebaseFirestore.Current.GetCollection("collection_name")
CrossFirebaseAuth.Current
CrossFirebaseStorage.Current.GetReferenceFromUrl(...)
CrossFirebaseCloudMessaging.Current.GetTokenAsync()
```

### Critical quirk — proxy classes

Plugin.Firebase uses reflection-based deserialization that is **incompatible** with the `[FirestoreData]` entities in `LARGA.Shared.Models`. Mobile service files each define their own local proxy classes implementing `IFirestoreObject` with `[FirestoreProperty]` attributes.

### Critical quirk — DateTime bug

`FirestoreDateTimeFix.Apply()` (called at startup) patches a Plugin.Firebase 4.0.0 Android bug where Firestore timestamps deserialize near year 1601 due to `DateTime.FromFileTimeUtc` being misapplied to Unix milliseconds.

---

## 4. Admin SDK Setup (ManagerWeb & SeedTool)

### Credentials — `appsettings.Local.json` (gitignored)

```json
{
  "Firestore": {
    "ProjectId": "larga-blmtaxi",
    "CredentialsPath": "C:\\Users\\Kaila\\secrets\\larga-blmtaxi-firebase-adminsdk-fbsvc-50b22df059.json"
  }
}
```

Falls back to Application Default Credentials (ADC) if `CredentialsPath` is absent.

### Registration in `Program.cs`

```csharp
builder.Services.AddSingleton<Lazy<FirestoreDb>>(sp => new Lazy<FirestoreDb>(() => {
    var cfg = sp.GetRequiredService<IConfiguration>();
    var projectId = cfg["Firestore:ProjectId"];
    var credPath = cfg["Firestore:CredentialsPath"];
    if (!string.IsNullOrEmpty(credPath))
        return new FirestoreClientBuilder { GoogleCredential = ... }.Build() -> FirestoreDb;
    return FirestoreDb.Create(projectId);
}));

builder.Services.AddSingleton<Lazy<FirebaseAuth>>(sp => new Lazy<FirebaseAuth>(() => {
    FirebaseApp.Create(new AppOptions { Credential = ... });
    return FirebaseAuth.DefaultInstance;
}));
```

Both are `Lazy<T>` — credential failures surface on first use, not at startup.

---

## 5. Dependency Injection — LARGA.MobileApp (`MauiProgram.cs`)

### Services (all Singleton)

| Interface | Implementation |
|---|---|
| `IFirebaseAuthService` | `FirebaseAuthService` |
| `IChatService` | `ChatService` |
| `IShiftManagementService` | `ShiftManagementService` |
| `INotificationService` | `NotificationService` |
| `IMaintenanceService` | `MaintenanceService` |
| `IFuelService` | `FuelService` |
| `MobileFinancialLedgerService` | (concrete, no interface) |
| `IOcrService` | `AndroidOcrService` (Android only, `#if ANDROID`) |

### ViewModels

- All registered as **Transient** except:
- `ActiveShiftViewModel` — **Singleton** (shift state must survive tab switches)

### Views

All Page classes registered as **Transient**.

---

## 6. Dependency Injection — LARGA.ManagerWeb (`Program.cs`)

### Singletons

| Service |
|---|
| `Lazy<FirestoreDb>` |
| `Lazy<FirebaseAuth>` |
| `FleetReportingService` |
| `DriverManagementService` |
| `FinancialLedgerService` |
| `GarageService` |
| `AlertService` |
| `InventoryAuditService` |
| `FuelVerificationService` |

### Hosted Service

| Type | Purpose |
|---|---|
| `IdleAlertMonitorService` (`BackgroundService`) | Polls `FleetReportingService.GetIdleDriversAsync()` every 3 min, creates `system_alerts` rows for idle drivers |

### Blazor

```csharp
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
```

---

## 7. Service Layer — Where Each Service Lives

### LARGA.SharedCore/Services/ (shared by both clients)

**Client-SDK services (Plugin.Firebase — mobile only at runtime):**

| Service | Interface | Firestore Collections Touched |
|---|---|---|
| `FirebaseAuthService` | `IFirebaseAuthService` | `users` (role lookup) |
| `ChatService` | `IChatService` | `chats/{driverId}/messages` |
| `ShiftManagementService` | `IShiftManagementService` | `shifts`, `shift_schedules`, `taxis`, `boundary_payments`, `system_configs/global` |
| `NotificationService` | `INotificationService` | `users` (fcmToken write) |
| `MaintenanceService` | `IMaintenanceService` | `maintenance_logs` |
| `FuelService` | `IFuelService` | `fuel_logs` |
| `PhotoStorageService` | `IPhotoStorageService` | Firebase Storage (not Firestore) |
| `BoundaryPaymentRollup` | (static) | No direct I/O — pure domain logic |

**Admin-SDK services (Google.Cloud.Firestore — ManagerWeb only at runtime):**

| Service | Firestore Collections Touched |
|---|---|
| `FleetReportingService` | `shifts`, `boundary_payments`, `taxis`, `gps_telemetry`, `fuel_logs`, `maintenance_logs`, `system_configs/global`, `audit_logs` |
| `FinancialLedgerService` | `boundary_payments`, `debt_adjustments`, `shifts`, `users`, `system_configs/global` |
| `DriverManagementService` | `users`, `shifts`, `shift_schedules`, `maintenance_logs`, `handover_checklists` |
| `GarageService` | `maintenance_logs`, `taxis`, `maintenance_parts_used`, `routine_checks` |
| `AlertService` | `system_alerts` |
| `InventoryAuditService` | `spare_parts`, `audit_logs`, `maintenance_parts_used` |
| `FuelVerificationService` | `fuel_logs`, `shifts`, `users` |

### LARGA.MobileApp/Services/ (mobile-only)

| Service | Purpose |
|---|---|
| `MobileFinancialLedgerService` | Manager Quick Ledger — delegates reads to `LedgerFirestore`, logic to `QuickLedgerCalculator` |
| `LedgerFirestore` (static) | All Firestore reads for ledger screens using Plugin.Firebase proxy classes |
| `QuickLedgerCalculator` (static) | Pure financial logic; Firebase-free; used by tests |
| `AndroidOcrService` | ML Kit text recognition (Android platform, `Platforms/Android/Services/`) |
| `DriverLicenseTextParser` (static) | Philippine LTO license OCR parser |
| `ReceiptOcrParser` (static) | GCash receipt OCR parser (amount, date, reference) |
| `FirestoreDateTimeFix` (static) | Plugin.Firebase timestamp bug workaround |
| `LicenseStatusHelper` (static) | Client-side license expired/expiring/active logic |
| `MapFocusRequest` (static) | In-memory handoff: Alert Center SOS tap → Fleet Map pan |
| `MapTilerConfig` (static) | Holds MapTiler API key for Mapsui tile rendering |
| `LedgerLog` (static) | Debug logging for ledger operations |

---

## 8. Navigation & Routing — LARGA.MobileApp

### Shell structure (`AppShell.xaml`)

```
Root: LandingPage
  ├── driver-dashboard (TabBar)
  │     ├── Tab: DriverDashboardPage (Home)
  │     ├── Tab: LedgerPage
  │     ├── Tab: ReportsPage
  │     └── Tab: ProfilePage (driver)
  └── manager-dashboard (TabBar)
        ├── Tab: ManagerDashboardPage (Fleet Map)
        ├── Tab: ManagerChatsPage
        ├── Tab: ManagerLedgerPage
        ├── Tab: AlertCenterPage
        └── Tab: ManagerProfilePage
```

`FlyoutBehavior="Disabled"` — no hamburger menu.

### Modal/stack routes registered in `AppShell.xaml.cs`

All auth, pre-shift, end-shift, driver management detail, and receipt/scan pages are registered as shell routes (navigable via `Shell.Current.GoToAsync("//route")`).

### FCM notification routing

`App.xaml.cs` — on `NotificationTapped`, if type is `pre_shift_reminder`:
```csharp
Shell.Current.GoToAsync("//PreShiftStep1Page");
```

---

## 9. Key NuGet Packages

### LARGA.MobileApp

| Package | Purpose |
|---|---|
| `Plugin.Firebase.Core 4.1.0` | Firebase initialization |
| `Plugin.Firebase.CloudMessaging 4.0.1` | FCM push notifications |
| `Plugin.Firebase.Auth` | Email/password auth (client SDK) |
| `Plugin.Firebase.Firestore` | Firestore client reads/writes |
| `Plugin.Firebase.Storage` | Cloud Storage uploads |
| `Camera.MAUI` | Camera capture (receipts, odometer, license) |
| `CommunityToolkit.Maui` | MAUI helpers and controls |
| `CommunityToolkit.Mvvm` | `ObservableObject`, `[RelayCommand]`, `WeakReferenceMessenger` |
| `Google.Cloud.Firestore 4.4.0` | Admin SDK (ManagerWeb services reused in SharedCore) |
| `Mapsui.Maui` + `Mapsui.Nts` | Fleet map tile rendering |
| `Xamarin.Google.MLKit.TextRecognition` | OCR (Android only) |

### LARGA.ManagerWeb

| Package | Purpose |
|---|---|
| `Google.Cloud.Firestore 4.4.0` | Firestore admin SDK |
| `FirebaseAdmin 3.6.0` | Firebase Auth admin (create/reset drivers) |

---

## 10. Shared Utilities (LARGA.SharedCore)

| Utility | Location | Purpose |
|---|---|---|
| `PhilippineTime` | `LARGA.SharedCore/PhilippineTime.cs` | UTC+8 fixed offset; `ToPhilippineTime()` extension on `DateTime` / `DateTime?` |
| `BoundaryPaymentRollup` | `LARGA.SharedCore/Services/BoundaryPaymentRollup.cs` | Groups `boundary_payments` by shift, computes shortfalls, builds daily debt snapshots |

---

## 11. Firestore Collections — Write/Read Ownership

| Collection | Primary Writers | Primary Readers |
|---|---|---|
| `users` | `SeedTool`, `DriverManagementService`, `NotificationService` | All services |
| `taxis` | `SeedTool` | `ShiftManagementService`, `FleetReportingService`, `GarageService`, `FleetRegistryViewModel` |
| `shifts` | `ShiftManagementService` | All services |
| `shift_schedules` | `ShiftManagementService`, `DriverManagementService` | `DriverManagementService` |
| `boundary_payments` | `ShiftManagementService` (clock-out), `FinancialLedgerService`, `MobileFinancialLedgerService` | `FinancialLedgerService`, `FleetReportingService`, `LedgerFirestore` |
| `debt_adjustments` | `FinancialLedgerService`, `MobileFinancialLedgerService` | `FinancialLedgerService`, `LedgerFirestore` |
| `chats/{driverId}/messages` | `ChatService` | `ChatService` |
| `fuel_logs` | `FuelService` | `FleetReportingService`, `FuelVerificationService`, `AlertCenterViewModel` |
| `maintenance_logs` | `MaintenanceService` | `FleetReportingService`, `DriverManagementService`, `GarageService` |
| `handover_checklists` | `PreShiftStep2ViewModel`, `EndShiftStep2ViewModel` (direct) | `DriverManagementService` |
| `gps_telemetry` | `ActiveShiftViewModel` (direct) | `FleetReportingService`, `FleetMapViewModel` |
| `emergency_alerts` | `ActiveShiftViewModel` (SOS, direct) | `FleetReportingService`, `AlertCenterViewModel` |
| `system_alerts` | `AlertService` via `IdleAlertMonitorService` | `AlertService`, `NotificationBell.razor`, `AlertCenterViewModel` |
| `system_configs/global` | `SeedTool` | `ShiftManagementService`, `FinancialLedgerService`, `FleetReportingService`, `LedgerFirestore` |
| `spare_parts` | `InventoryAuditService` | `InventoryAuditService` |
| `maintenance_parts_used` | `SeedTool`, `GarageService` | `InventoryAuditService` |
| `routine_checks` | `SeedTool`, `GarageService` | `GarageService` |
| `audit_logs` | `SeedTool`, `InventoryAuditService` | `InventoryAuditService`, `FleetReportingService` (CSV export) |

---

## 12. ViewModel Base Class Conventions

ViewModels are **not** standardized to a single base — three patterns coexist:

| Pattern | VMs Using It |
|---|---|
| `INotifyPropertyChanged` (manual) | Older Auth and Driver VMs |
| `BindableObject` (MAUI-native) | Mid-era Driver and Manager VMs |
| `ObservableObject` (CommunityToolkit.Mvvm) | Newer Manager VMs (`ManagerLedgerViewModel`, `RecordPaymentViewModel`, `ScanReceiptViewModel`) |

---

## 13. Direct Firestore Access (bypassing services)

Some ViewModels call `CrossFirebaseFirestore.Current` directly instead of going through a service:

| ViewModel | Why Direct |
|---|---|
| `LedgerViewModel` | Driver's own ledger reads |
| `PaymentHistoryViewModel` | Driver payment history reads |
| `DebtDetailViewModel` | Driver debt breakdown reads |
| `ProfileViewModel` | Driver profile reads/writes |
| `ReportsViewModel` | Driver defect/fuel report reads |
| `FuelReportDetailViewModel` | Fuel report detail reads |
| `DefectReportDetailViewModel` | Defect report detail reads |
| `DriverUpdateContactViewModel` | Phone number write |
| `PreShiftStep2ViewModel` | Handover checklist writes |
| `EndShiftStep2ViewModel` | Handover checklist writes |
| `ActiveShiftViewModel` | GPS telemetry writes, SOS alert writes |
| `DriverDashboardViewModel` | Shift state reads |
| `AlertCenterViewModel` | Three snapshot listeners (emergency_alerts, fuel_logs, maintenance_logs) |
| `FleetMapViewModel` | Real-time GPS telemetry reads |
| `FleetRegistryViewModel` | Taxi unit reads |
| `DriverManagementViewModel` | User roster reads |
| `ManagerDriverProfileViewModel` | User/shift/maintenance reads |
| `ManagerProfileViewModel` | Manager profile reads/writes |
| `UpdateContactNumberViewModel` | Phone number write |
