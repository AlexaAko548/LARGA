# LARGA

LARGA is BLM Taxi's serverless operations platform for drivers and managers in Talisay City, Cebu.

[Live dashboard](https://larga-managerweb-hoyynpheda-as.a.run.app/) | [Architecture](docs/ARCHITECTURE.md) | [Data model](docs/ERD.md)

## Overview

LARGA is built for BLM Taxi, a taxi operator in San Roque, Talisay City, Cebu. It replaces paper-based shift, boundary, maintenance, inventory, receipt, and emergency records with one real-time system.

It has three parts:

- The mobile app (LARGA.MobileApp): an Android app for drivers and managers.
- The web dashboard (LARGA.ManagerWeb): a browser dashboard for managers.
- The Firebase backend: Firestore, Authentication, Cloud Storage, and Cloud Messaging.

## Features

- Driver and shift management
- Digital handover inspections
- Boundary and arrears cashiering
- Maintenance and spare-parts inventory
- GPS tracking during shifts
- Fuel and odometer OCR
- SOS emergency workflows and manager alerts
- Messaging and push notifications
- Analytics and audit logs

## Tech stack

| Layer | Technology |
| --- | --- |
| Mobile app | .NET 9 MAUI (native XAML), Android target `net9.0-android`, CommunityToolkit.Maui 9.1.1, CommunityToolkit.Mvvm 8.4.2, Camera.MAUI 1.5.1 |
| Web dashboard | Blazor Server on ASP.NET Core (.NET 9), MudBlazor 9.8.0, QuestPDF 2026.9.1 |
| Shared libraries | LARGA.SharedCore and LARGA.Shared.Models (.NET 9), Google.Cloud.Firestore 4.4.0, FirebaseAdmin 3.6.0, Plugin.Firebase 4.x |
| Backend | Firebase: Cloud Firestore, Authentication, Cloud Storage, Cloud Messaging (FCM) |
| OCR | Google ML Kit through Xamarin.Google.MLKit.TextRecognition 116.0.0.9 (Android) |
| Maps | MapTiler tiles, Mapsui.Maui 5.1.0 in the mobile app |
| Hosting | Google Cloud Run (web dashboard), Docker image `mcr.microsoft.com/dotnet/aspnet:9.0` |
| CI | GitHub Actions (`windows-latest`) |
| Tests | xUnit 2.9.2 (.NET); mocha 12.0.3 with @firebase/rules-unit-testing 5.0.2 and the Firebase emulator (rules) |

## Architecture

LARGA uses a serverless N-tier design. The clients hold the screens, the shared libraries hold the contracts and logic, and Firebase is the backend.

```text
LARGA.MobileApp (native .NET MAUI XAML)
                    │
LARGA.ManagerWeb (Blazor / ASP.NET Core)
                    │
       LARGA.SharedCore + LARGA.Shared.Models
                    │
 Firebase BaaS: Firestore, Authentication, Cloud Storage, FCM
```

There is no custom REST API and no separate database server. Both clients use the same Firebase project and data model.

- [Architecture details](docs/ARCHITECTURE.md): dependency boundaries, entities, and services.
- [Data model (ERD)](docs/ERD.md): Firestore collections and relations.

## Repository structure

```text
LARGA.sln
├── LARGA.MobileApp/        Mobile app: .NET MAUI, Android
│   ├── Views/              XAML pages (Auth, Driver, Manager, Shared)
│   ├── ViewModels/         View models
│   ├── Services/           Mobile-only services
│   └── Platforms/Android/  Android startup, manifest, OCR
├── LARGA.ManagerWeb/       Web dashboard: Blazor Server
│   ├── Components/         Pages, layouts, shared components
│   ├── Services/           Web-only services
│   └── wwwroot/            Static files and Firebase web sign-in scripts
├── LARGA.SharedCore/       Shared services and application rules
├── LARGA.Shared.Models/    Firestore entities and converters
├── LARGA.SeedTool/         Console tool that seeds Firestore test data
├── LARGA.Tests/            xUnit tests, JavaScript rules tests, fixtures
├── tests/
│   ├── rules/              Firestore and Storage security-rules tests (Node)
│   ├── odometer/           Sample dashboard photos for the odometer OCR tests
│   └── receipts/           Sample fuel receipt photos used to capture OCR fixtures
├── tools/loadtest/         Scripts for the 5-emulator load test
├── deploy/                 Cloud Run deploy script and Dockerfile
├── docs/                   ARCHITECTURE.md and ERD.md
├── .github/workflows/      CI pipeline
├── firebase.json           Firebase config for rules and indexes
├── firestore.rules         Firestore security rules
├── firestore.indexes.json  Firestore indexes
└── storage.rules           Cloud Storage security rules
```

## Getting started

### Prerequisites

1. Visual Studio 2022 17.14 or later, with the **.NET Multi-platform App UI development** and **ASP.NET and web development** workloads.
2. The .NET 9 SDK.
3. Android SDK (API 31 or later) and an Android emulator image. Use a Google APIs or Google Play image to test Firebase and ML Kit.
4. Git 2.40 or later.
5. Node.js 18 or later and Java 11 or later, only if you run the security-rules tests or the emulator tests.

### Clone

```powershell
git clone https://github.com/AlexaAko548/LARGA.git
cd LARGA
git checkout develop
```

### Local configuration

These files are not in Git. Ask the project owner or the Firebase project administrator for the development values.

| File | Where it goes | How to get it |
| --- | --- | --- |
| `google-services.json` | `LARGA.MobileApp/Platforms/Android/` | Firebase Console, Project settings, Your apps, Android. |
| `appsettings.Development.json` | `LARGA.ManagerWeb/` | Copy `appsettings.Development.json.example`. Get the map and API settings from the team. |
| `appsettings.Local.json` | `LARGA.ManagerWeb/` | Copy `appsettings.Local.json.example`. Set `Firestore:ProjectId` and `Firestore:CredentialsPath` to a service-account key stored outside the repository. Create the key in Firebase Console, Project settings, Service accounts, Generate new private key. |
| `firebase-config.js` | `LARGA.ManagerWeb/wwwroot/` | Copy `firebase-config.example.js`. Fill in the values from Firebase Console, Project settings, Your apps, Web. |
| `MapTilerConfig.cs` | `LARGA.MobileApp/Services/` | Copy `MapTilerConfig.cs.example`. The mobile app does not compile without it. Get the real key from the team. |

**Never commit these files or any service-account key.**

### Run the app

Mobile app:

1. Open `LARGA.sln` in Visual Studio and restore the NuGet packages.
2. Set `LARGA.MobileApp` as the startup project.
3. Pick an Android emulator or device and press Run.

Web dashboard:

1. Make sure `appsettings.Local.json` and `firebase-config.js` exist.
2. Set `LARGA.ManagerWeb` as the startup project.
3. Press Run.

### Sign in

The web dashboard needs a Firebase Auth user whose `users/{uid}.role` is `Manager` or `Assistant Manager`. Other roles cannot sign in. To fill Firestore with test data, see [LARGA.SeedTool/README.md](LARGA.SeedTool/README.md).

## Roles and permissions

Assistant Managers sign in from the Manager tile in the mobile app. They get Map, Chats, Ledger (Quick Ledger and recording payments), and Alerts.

| Feature | Driver | Assistant Manager | Manager |
| --- | --- | --- | --- |
| Shifts, handover inspections, fuel and odometer reports, own ledger | ✓ | — | — |
| Send an SOS | ✓ | — | — |
| Message the manager | ✓ | — | — |
| Web dashboard: drivers and shifts, SOS dispatch, garage, fuel logs | — | ✓ | ✓ |
| Financial ledger and recording payments | — | ✓ | ✓ |
| Message and call drivers | — | ✓ | ✓ |
| Approve or deny flagged clock-ins | — | ✓ | ✓ |
| Resolve an SOS | — | — | ✓ |
| Resolve a fuel flag | — | — | ✓ |
| Audit Logs | — | — | ✓ |
| Inventory | — | — | ✓ |
| Driver account edits | — | — | ✓ |
| Contact number setting | — | — | ✓ |

A phone denial of a clock-in may send the unit and its defect reports to the Garage. `firestore.rules` tie that to the denial.

## Testing

Run these from the repository root.

### Unit tests

```powershell
dotnet test LARGA.Tests/LARGA.Tests.csproj
```

A plain run skips the emulator tests and needs no network or credentials.

### Emulator-backed integration tests

These run against a local Firestore emulator. Start the emulator first, then set `LARGA_EMULATOR`:

```powershell
$env:LARGA_EMULATOR = "1"
dotnet test LARGA.Tests/LARGA.Tests.csproj
```

The emulator start command and the full details are in [LARGA.Tests/README.md](LARGA.Tests/README.md).

### Security-rules tests

```powershell
cd tests/rules
npm install
npm test
```

`npm test` starts the Firestore and Storage emulators, runs `firestore.test.js` and `storage.test.js`, then stops the emulators. See [tests/rules/README.md](tests/rules/README.md).

### Continuous integration

GitHub Actions runs on every push and pull request to `main` and `develop`. It builds `LARGA.sln` in Release and runs `dotnet test` on the solution. It does not run the emulator tests or the rules tests.

## Build and deployment

Releases are done by the team lead. The commands below are documentation only.

### Web dashboard (Cloud Run)

`deploy/deploy-cloudrun.ps1` publishes `LARGA.ManagerWeb` in Release, removes the local settings files from the output, and deploys it to Cloud Run. Cloud Build builds the image from `deploy/Dockerfile`. Docker is not needed on your PC.

Prerequisites:

- The Google Cloud CLI (`gcloud`), signed in to an account that owns the `larga-blmtaxi` project.
- `LARGA.ManagerWeb/wwwroot/firebase-config.js` exists. The script stops without it.

```powershell
.\deploy\deploy-cloudrun.ps1
```

Defaults from the script:

| Setting | Value |
| --- | --- |
| Project | `larga-blmtaxi` |
| Service | `larga-managerweb` |
| Region | `asia-southeast1` (Singapore) |
| Minimum instances | 1, always running, with CPU outside requests |
| Maximum instances | 1, with session affinity |
| Memory and timeout | 512Mi, 60-minute request timeout |

One always-on instance is needed because the SOS push and the background checks run inside the web app. The script explains the reasons in its header.

### Android APK

Build a signed Release APK from the repository root. The project sets the application ID `com.blmtaxi.larga.mobile`, version `1.0` (build 3), and a minimum Android API of 23.

```powershell
dotnet publish LARGA.MobileApp/LARGA.MobileApp.csproj -f net9.0-android -c Release `
  -p:AndroidPackageFormat=apk `
  -p:AndroidKeyStore=true `
  -p:AndroidSigningKeyStore=<path-to-keystore> `
  -p:AndroidSigningKeyAlias=<alias> `
  -p:AndroidSigningKeyPass=env:LARGA_KEY_PASS `
  -p:AndroidSigningStorePass=env:LARGA_STORE_PASS
```

The APK is written under `LARGA.MobileApp/bin/Release/net9.0-android/publish/`.

The keystore, its passwords, and `google-services.json` stay out of the repository. `google-services.json` and `MapTilerConfig.cs` must be present on the build machine.

### Firebase rules and indexes

`firebase.json` points to `firestore.rules`, `firestore.indexes.json`, and `storage.rules`.

```powershell
firebase deploy --only firestore:rules,firestore:indexes,storage --project larga-blmtaxi
```

## Repository tools

| Tool | What it does | Docs |
| --- | --- | --- |
| `LARGA.SeedTool` | Seeds the Firestore project with repeatable test data. Re-running it overwrites the same records. | [LARGA.SeedTool/README.md](LARGA.SeedTool/README.md) |
| `tools/loadtest` | PowerShell scripts that run the 5 test drivers as Android emulators for the load test. | [tools/loadtest/README.md](tools/loadtest/README.md) |
| `tests/rules` | Firestore and Storage security-rules tests on the local emulator. | [tests/rules/README.md](tests/rules/README.md) |

## Contributing

LARGA follows GitFlow:

- `main`: production and stable. It takes reviewed release or hotfix merges only.
- `develop`: the integration branch for finished work.
- `feature/*`: branches from `develop`, merged back by pull request. Example: `feature/REQ-4.1-driver-shift`.
- `release`: branches may be cut from `develop`.
- `hotfix`: urgent fixes branch from `main` and merge back into both `main` and `develop`.

Pull request rules:

- Do not commit directly to `main` or `develop`.
- A pull request needs a review, passing CI, and resolved conversations.
- No force pushes.
- Before opening a pull request, run `dotnet build LARGA.sln` and `dotnet test LARGA.Tests/LARGA.Tests.csproj`.
- Make sure no credential files, `bin/`, or `obj/` files are staged.

Use [Conventional Commits](https://www.conventionalcommits.org/):

```text
feat(auth): add manager role validation
fix(ocr): handle unreadable receipt
```

## Project

- Client: BLM Taxi, San Roque, Talisay City, Cebu
- Team: Maykaila Joan J. Arda, Luigi Adrian T. Hatamosa, Karl Emmanuel Medina, Alexa Rose I. Miñoza
- Adviser: Christine D. Bandalan, MEng
- Capstone project, Department of Computer, Information Sciences and Mathematics, University of San Carlos
- Live web dashboard: https://larga-managerweb-hoyynpheda-as.a.run.app/
