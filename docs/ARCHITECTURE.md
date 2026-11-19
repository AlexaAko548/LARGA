# LARGA Architecture

This page holds the architecture detail that used to live in the main [README](../README.md). For the data model, see [ERD.md](ERD.md).

## N-tier overview

LARGA uses a serverless N-tier architecture.

- The clients hold the presentation code.
- The shared libraries hold the contracts and the application logic.
- Firebase provides the backend as a service (BaaS).

There is no custom REST API and no separate database server in this repository.

```text
LARGA.MobileApp (native .NET MAUI XAML)
                    │
LARGA.ManagerWeb (Blazor / ASP.NET Core)
                    │
       LARGA.SharedCore + LARGA.Shared.Models
                    │
 Firebase BaaS: Firestore, Authentication, Cloud Storage, FCM
```

## Firebase services

Both clients use the same Firebase project and data model.

- **Cloud Firestore** stores users, shifts, vehicles, ledgers, maintenance, messages, alerts, telemetry, and audit records.
- **Firebase Authentication** handles sign-in and password recovery. Manager access is also checked against `users/{uid}.role`.
- **Cloud Storage for Firebase** stores inspection photos and receipt images.
- **Firebase Cloud Messaging (FCM)** delivers push notifications, shift reminders, and emergency alerts.

## The two clients

The mobile app (`LARGA.MobileApp`) is strictly native .NET MAUI. Screens are XAML pages with C# code-behind and view models. It uses no web views and no Xamarin.Forms.

Android is the primary target. Android OCR uses Google ML Kit and is registered through the Android platform layer.

The web dashboard (`LARGA.ManagerWeb`) is a Blazor ASP.NET Core application. It uses interactive server components for workstation dashboards. The web host uses Firebase Admin SDK credentials for server-side Firestore and Authentication administration.

## Project dependency boundaries

```text
LARGA.Shared.Models  (entities and Firestore mappings)
          ↑
LARGA.SharedCore      (shared service contracts and application logic)
       ↑       ↑       ↑
 MobileApp  ManagerWeb  SeedTool
```

The references are one-way. There is no circular project dependency.

- The mobile app, web dashboard, and seed tool reference the shared libraries.
- The shared libraries do not reference either client.
- Android implementation files live under `LARGA.MobileApp/Platforms/Android`. They are not in `LARGA.SharedCore`.

### Plugin.Firebase note

`LARGA.SharedCore` uses `Plugin.Firebase` abstractions in `FirebaseAuthService` and `ChatService`. It is reusable application logic, but it is not a fully platform-neutral library.

Keep this boundary when you extend the solution:

- Do not add Android namespaces or UI dependencies to `LARGA.SharedCore`.
- If a future server-side implementation needs to share those services, extract provider-neutral interfaces into the core.
- Keep the mobile Firebase adapters in `LARGA.MobileApp`.
- Keep the web-only Firebase Admin adapters in `LARGA.ManagerWeb`.

## Entities

The principal entities are in `LARGA.Shared.Models/Entities`. Firestore attributes and value converters live beside them.

- `UserProfile`
- `TaxiUnit`
- `ShiftSchedule`
- `ShiftLog`
- `BoundaryPayment`
- `DebtAdjustment`
- `FuelLog`
- `GpsTelemetry`
- `EmergencyAlert`
- `ChatMessage`
- `HandoverChecklist`
- `RoutineCheckItem`
- `MaintenanceRecord`
- `MaintenancePartsUsed`
- `SparePart`
- `SystemConfig`
- `AuditLog`

## Services

Services live in `LARGA.SharedCore/Services`. They cover these areas:

- authentication
- chat
- shift management
- notifications
- fleet reporting
- driver management
- garage management
- financial ledger operations

Keep data shape changes in the shared models first. Then both clients compile against the same contract.
