# LARGA

LARGA is BLM Taxi's serverless operations platform for drivers and managers in Talisay City, Cebu. It replaces paper-based shift, boundary, maintenance, inventory, receipt, and emergency workflows with a real-time system.

## Architecture

LARGA uses a serverless N-tier architecture. Clients contain presentation concerns, shared libraries contain contracts and application logic, and Firebase provides the backend-as-a-service (BaaS). There is no custom REST API or separate database server in this repository.

```text
LARGA.MobileApp (native .NET MAUI XAML)
                    │
LARGA.ManagerWeb (Blazor / ASP.NET Core)
                    │
       LARGA.SharedCore + LARGA.Shared.Models
                    │
 Firebase BaaS: Firestore, Authentication, Cloud Storage, FCM
```

Both clients use the same Firebase project and data model:

- **Cloud Firestore** stores users, shifts, vehicles, ledgers, maintenance, messages, alerts, telemetry, and audit records.
- **Firebase Authentication** handles sign-in and password recovery. Manager access is also checked against `users/{uid}.role`.
- **Cloud Storage for Firebase** stores inspection photos and receipt images.
- **Firebase Cloud Messaging (FCM)** delivers push notifications, shift reminders, and emergency alerts.

`LARGA.MobileApp` is strictly native .NET MAUI: screens are XAML pages with C# code-behind/view models, not web views and not Xamarin.Forms. Android is the primary deployment target; Android OCR uses Google ML Kit and is registered through the Android platform layer. `LARGA.ManagerWeb` is a Blazor ASP.NET Core application using interactive server components for workstation dashboards. The web host uses Firebase Admin SDK credentials for server-side Firestore and Authentication administration.

### Project dependency boundaries

```text
LARGA.Shared.Models  (entities and Firestore mappings)
          ↑
LARGA.SharedCore      (shared service contracts and application logic)
       ↑       ↑       ↑
 MobileApp  ManagerWeb  SeedTool
```

The references are one-way and currently have no circular project dependency: the mobile app, web app, and seed tool reference the shared libraries; the shared libraries do not reference either client. Android implementation files are correctly located under `LARGA.MobileApp/Platforms/Android` and are not present in `LARGA.SharedCore`.

One boundary to preserve when extending the solution: `LARGA.SharedCore` currently uses `Plugin.Firebase` abstractions in `FirebaseAuthService` and `ChatService`. It is therefore reusable application logic, but not a completely platform-neutral library. Do not add Android namespaces or UI dependencies to it. If a future server-side implementation needs to share those services, extract provider-neutral interfaces into the core and keep mobile Firebase adapters in `LARGA.MobileApp`; keep web-only Firebase Admin adapters in `LARGA.ManagerWeb`.

## Repository structure

```text
LARGA.sln
├── LARGA.MobileApp/              Native MAUI client
│   ├── Views/Auth/               Landing, login, and password recovery XAML
│   ├── Views/Driver/             Shift, profile, ledger, reports, OCR, and chat
│   ├── Views/Manager/            Manager dashboard and alert-center XAML
│   ├── ViewModels/Auth|Driver|Manager/
│   ├── Services/                 Mobile interfaces such as IOcrService
│   └── Platforms/Android/        MainActivity, Firebase startup, Android OCR,
│                                  and google-services.json
├── LARGA.ManagerWeb/             Blazor ASP.NET Core dashboard
│   ├── Components/Pages/         Dashboard, shifts, garage, ledger, and auth pages
│   ├── Components/Layout/        Main and authentication layouts
│   ├── Components/Shared/        Reusable dashboard components
│   └── Program.cs                 Web host and Firebase Admin registration
├── LARGA.Shared.Models/           Shared data contracts
│   └── Entities/                 Firestore entities and converters
├── LARGA.SharedCore/              Shared services and application logic
│   ├── Services/                 Auth, chat, shifts, notifications, fleet,
│   │                              garage, driver, and financial services
│   └── Models/                   Dashboard, garage, ledger, and shift DTOs
├── LARGA.SeedTool/                Console tool for repeatable Firestore seed data
└── docs/                          Design documentation, including the ERD
```

The principal entities in `LARGA.Shared.Models/Entities` are `UserProfile`, `TaxiUnit`, `ShiftSchedule`, `ShiftLog`, `BoundaryPayment`, `DebtAdjustment`, `FuelLog`, `GpsTelemetry`, `EmergencyAlert`, `ChatMessage`, `HandoverChecklist`, `RoutineCheckItem`, `MaintenanceRecord`, `MaintenancePartsUsed`, `SparePart`, `SystemConfig`, and `AuditLog`. Firestore attributes and value converters live beside these entities.

Services in `LARGA.SharedCore/Services` include authentication, chat, shift management, notifications, fleet reporting, driver management, garage management, and financial ledger operations. Keep data shape changes in the shared models first so both clients compile against the same contract.

## Local setup: Visual Studio 2022

Install:

1. Visual Studio 2022 17.14 or later with **.NET Multi-platform App UI development** and **ASP.NET and web development** workloads.
2. The .NET 9 SDK, Android SDK/API 31 or later, an Android emulator or device, and Git 2.40 or later.
3. The Android emulator's Google APIs/Google Play image if Firebase and ML Kit are being tested locally.

Clone and open `LARGA.sln`:

```powershell
git clone https://github.com/AlexaAko548/LARGA.git
cd LARGA
git checkout develop
```

Restore NuGet packages, choose `LARGA.MobileApp` with an Android emulator (or `LARGA.ManagerWeb` for the web dashboard) as the startup project, and build the solution. The seed tool README contains the complete Firestore test-data workflow.

### Local secrets

These files are intentionally excluded from source control. Obtain the development values from the project owner or Firebase project administrator; do not generate replacements from production credentials or commit secrets.

- Download the Android app configuration from Firebase Console → Project settings → Your apps → Android → `google-services.json`. Copy it to `LARGA.MobileApp/Platforms/Android/google-services.json`. The project only includes it for the Android target.
- Copy `LARGA.ManagerWeb/appsettings.Development.json.example` to `LARGA.ManagerWeb/appsettings.Development.json` and obtain any local map/API settings from the team. For server-side Firestore/Admin access, copy `appsettings.Local.json.example` to `appsettings.Local.json` and set `Firestore:ProjectId` and `Firestore:CredentialsPath` to a service-account key stored outside the repository. Never commit the key.
- A service-account key can be generated in Firebase Console → Project settings → Service accounts → **Generate new private key**. Restrict the key to local development and revoke it if exposed.

Manager login requires a Firebase Auth account whose Firestore user profile has `role` set to `Manager` (case-insensitive). Valid credentials without the profile/role do not grant web access.

## GitFlow

- `main` is production/stable and receives reviewed release or hotfix merges.
- `develop` is the integration branch for completed work.
- `feature/*` branches start from `develop` and merge back through a pull request, for example `feature/REQ-4.1-driver-shift`.
- Release branches may be cut from `develop`; urgent hotfixes branch from `main` and are merged back into both protected branches.

Do not commit directly to `main` or `develop`. Pull requests require review, passing CI, resolved conversations, and no force pushes. Use Conventional Commits such as `feat(auth): add manager role validation` or `fix(ocr): handle unreadable receipt`.

## Verification checklist

From the repository root, run `dotnet build LARGA.sln`. The solution should restore and build all five projects. Before a pull request, also test the affected client with its real Firebase development configuration and ensure that no credential files, `bin/`, or `obj/` files are staged.

## Main capabilities

Driver and shift management, digital handover inspections, boundary and arrears cashiering, maintenance and spare-parts inventory, contextual GPS tracking, fuel/odometer OCR, SOS workflows, manager alerts, messaging, analytics, and audit logs.

## Project

Capstone project for BLM Taxi by the Department of Computer, Information Sciences and Mathematics, University of San Carlos.
