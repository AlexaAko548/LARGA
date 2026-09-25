# LARGA — System Architecture

## 1. System Overview

LARGA is a **serverless, cross-platform, N-tier taxi operations management system** built for BLM Taxi (Talisay City, Cebu). It digitizes paper-based shift, boundary/arrears, maintenance, inventory, fuel-receipt, and emergency workflows into a real-time, Firebase-backed platform.

There is **no custom REST API and no self-hosted database server** in this repository. All persistence, authentication, file storage, and push messaging are delegated to **Firebase** as a Backend-as-a-Service (BaaS). Both clients (mobile and web) talk directly to the same Firebase project using platform-appropriate SDKs (client SDKs on mobile, Firebase Admin SDK on the web server).

```text
???????????????????????????????     ???????????????????????????????
?  LARGA.MobileApp             ?     ?  LARGA.ManagerWeb            ?
?  Native .NET MAUI (Android-  ?     ?  Blazor / ASP.NET Core        ?
?  first), Drivers + tactical  ?     ?  (Interactive Server),        ?
?  Managers                    ?     ?  desktop admin dashboard      ?
????????????????????????????????     ????????????????????????????????
                ?                                     ?
                ???????????????????????????????????????
                                 ?
                  ????????????????????????????????
                  ? LARGA.SharedCore              ?
                  ? Business logic + Firebase      ?
                  ? cloud data-access services     ?
                  ????????????????????????????????
                                 ?
                  ????????????????????????????????
                  ? LARGA.Shared.Models            ?
                  ? Domain models / Firestore       ?
                  ? entities & converters           ?
                  ????????????????????????????????
                                 ?
                  ????????????????????????????????
                  ? Firebase BaaS                  ?
                  ? Firestore · Auth · Storage · FCM?
                  ??????????????????????????????????
```

## 2. Monorepo Topology

The solution (`LARGA.sln`) is a single-repo, multi-project layout with **strictly one-directional** project references (no circular dependencies):

```text
LARGA.Shared.Models   (bottom layer: entities + Firestore mappings)
        ?
LARGA.SharedCore      (service contracts + application/business logic)
   ?        ?        ?
MobileApp  ManagerWeb  SeedTool
```

### `LARGA.MobileApp`
- Native **.NET MAUI** application (XAML pages + C# code-behind + MVVM ViewModels — **not** Xamarin.Forms, **not** a web view/hybrid app).
- **Android is the primary/first-class deployment target.** Other MAUI heads (Windows, MacCatalyst, Tizen) exist only as scaffolding from the project template and are not part of the active product surface.
- Serves two personas:
  - **Drivers**: shift lifecycle, ledger/boundary payments, fuel reporting, vehicle defect reporting, messaging.
  - **Managers (on-the-go / tactical use)**: `ManagerDashboardPage`, `AlertCenterPage`.
- Structure:
  - `Views/Auth`, `Views/Driver`, `Views/Manager` — XAML pages.
  - `ViewModels/Auth`, `ViewModels/Driver`, `ViewModels/Manager` — MVVM view models.
  - `Services/` — mobile-only interfaces (e.g., `IOcrService`).
  - `Platforms/Android/` — `MainActivity`, `MainApplication`, Firebase Android bootstrap (`CrossFirebase.Initialize`), and `google-services.json` (excluded from source control).

### `LARGA.ManagerWeb`
- **Blazor ASP.NET Core** application using **Interactive Server** render mode, targeting desktop/workstation use by office managers for heavier administrative workflows (bulk views, tables, ledgers) that are impractical on a phone screen.
- Structure:
  - `Components/Pages/` — routable pages (`Dashboard`, `DriverShifts`, `FinancialLedger`, `Garage`, `Login`, `Register`, `RegisterPassword`, `Landing`, etc.).
  - `Components/Layout/` — `MainLayout`, `AuthLayout`, `NavMenu`.
  - `Components/Shared/` — reusable components (e.g., `DashboardSidebar`, `SimpleBarChart`).
  - `Program.cs` — web host bootstrap and **Firebase Admin SDK** registration for server-side Firestore/Auth administration.

### `LARGA.Shared.Models`
- Plain C# class library containing **Firestore entity models** (`[FirestoreData]` POCOs) and custom `IFirestoreConverter<T>` implementations for enums, decimals, and lenient string parsing.
- No business logic, no platform dependencies. This is the single source of truth for the data shape both clients compile against.

### `LARGA.SharedCore`
- Centralized business logic and Firebase cloud data-access services, consumed by both `MobileApp` and `ManagerWeb`.
- Contains services such as `FirebaseAuthService`, `ChatService`, `ShiftManagementService`, `NotificationService`, `FleetReportingService`, `DriverManagementService`, `GarageService`, `MaintenanceService`, `FinancialLedgerService`, plus DTO/view models under `Models/`.
- **Boundary rule**: `LARGA.SharedCore` uses `Plugin.Firebase` abstractions (mobile-oriented) in some services (e.g., `FirebaseAuthService`, `ChatService`), so it is reusable application logic but **not fully platform-neutral**. Do not add Android-specific namespaces or UI dependencies here. If server-side (web) equivalents are needed that can't reuse the mobile Firebase plugin, keep Firebase Admin adapters in `LARGA.ManagerWeb` and provider-neutral contracts in `SharedCore`.

### `LARGA.SeedTool`
- A console utility for repeatable Firestore seed/test data generation, used for local development and demos.

## 3. Backend & Third-Party Integrations

| Integration | Purpose | Where used |
|---|---|---|
| **Firebase Firestore** | Primary NoSQL document database for all operational data (users, shifts, ledgers, maintenance, fuel, alerts, chat, audit, config) | `SharedCore` services (client SDK via `Plugin.Firebase`), `ManagerWeb` (Firebase Admin SDK) |
| **Firebase Authentication** | Sign-in, password recovery, session management. Manager web access additionally requires `users/{uid}.role == "Manager"` (case-insensitive) in Firestore | `FirebaseAuthService`, `ManagerWeb` Login/Register pages |
| **Firebase Cloud Storage** | Stores inspection photos (`HandoverChecklist.ScratchesPhotoUrl`, `FuelDashboardUrl`), fuel receipt images (`FuelLog.ReceiptImageUrl`), and vehicle defect photos | Driver mobile capture flows |
| **Firebase Cloud Messaging (FCM)** | Push notifications, shift reminders, alerts | `NotificationService` |
| **Google ML Kit (on-device OCR)** | Reads vehicle odometer values and fuel receipt text directly on the Android device, avoiding manual data entry errors | `IOcrService` + Android platform OCR implementation, consumed by `OdometerScanPage` and `ScanFuelReceiptPage` |
| **MapLibre / MapTiler** *(telematics/mapping)* | `[PENDING IMPLEMENTATION]` — planned for real-time GPS fleet visualization (tactical map, Manager Web live map). **No map package references or map view components currently exist in the codebase.** | N/A yet |
| **Android Telephony API** *(call overrides)* | `[PENDING IMPLEMENTATION]` — planned to allow drivers/managers to make emergency/dispatch calls directly from within the app. **No `TelephonyManager`/`PhoneStateListener` code currently exists.** | N/A yet |

## 4. Data Flow Pattern

1. A driver performs an action on `LARGA.MobileApp` (e.g., starts a shift, submits a fuel report).
2. The mobile ViewModel calls into a `LARGA.SharedCore` service (e.g., `ShiftManagementService`), passing/receiving `LARGA.Shared.Models` entities.
3. The service reads/writes Firestore documents directly via the Firebase client SDK (`Plugin.Firebase`).
4. `LARGA.ManagerWeb` observes/queries the same Firestore collections server-side via the Firebase Admin SDK, rendering Blazor Interactive Server components that update in near-real time.
5. Critical events (new alerts, shift status changes) are additionally pushed via FCM to relevant manager devices.

## 5. Platform & Framework Notes

- All projects target **.NET 9**.
- `LARGA.MobileApp` must only be discussed/extended in terms of **.NET MAUI** patterns — Xamarin.Forms is not applicable to this codebase.
- `LARGA.ManagerWeb` uses **Blazor** (Interactive Server components) — prefer Blazor idioms over Razor Pages or MVC when extending it.
