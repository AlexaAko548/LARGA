# LARGA — Workflow & Testing

## 1. Branching Strategy (Strict GitFlow)

LARGA follows a strict **GitFlow** branching model:

| Branch | Purpose |
|---|---|
| `main` | Production/stable. Only receives reviewed **release** or **hotfix** merges. |
| `develop` | Integration branch for completed feature work. |
| `feature/*` | Branches off `develop` for new work, e.g. `feature/REQ-4.1-driver-shift`, `feature/fuel-report`. Merged back into `develop` via pull request. |
| `release/*` | Cut from `develop` to stabilize a release candidate. |
| `hotfix/*` | Branches from `main` for urgent production fixes; merged back into **both** `main` and `develop`. |

### Rules

- **Direct commits to `main` and `develop` are blocked.** All changes must go through a pull request.
- Pull requests require:
  - Code review approval.
  - Passing CI.
  - All review conversations resolved.
  - No force pushes.
- **Commit message convention**: [Conventional Commits](https://www.conventionalcommits.org/), e.g.:
  - `feat(auth): add manager role validation`
  - `fix(ocr): handle unreadable receipt`
  - `chore(deps): bump firebase sdk`

## 2. Local Development Setup

- IDE: Visual Studio 2022 (17.14+) with **.NET Multi-platform App UI development** and **ASP.NET and web development** workloads.
- SDK: .NET 9, Android SDK/API 31+, Android emulator or physical device (Google Play image required for Firebase/ML Kit testing), Git 2.40+.
- Secrets (never committed):
  - `LARGA.MobileApp/Platforms/Android/google-services.json` (Firebase Android app config).
  - `LARGA.ManagerWeb/appsettings.Development.json` (copied from `.example`).
  - `LARGA.ManagerWeb/appsettings.Local.json` (copied from `.example`) — sets `Firestore:ProjectId` and `Firestore:CredentialsPath` pointing to a Firebase service-account key stored **outside** the repository.
- `LARGA.SeedTool` provides repeatable Firestore seed/test data — see its own README for the full workflow.

## 3. Verification Checklist (Pre-PR)

1. Run `dotnet build LARGA.sln` from the repository root — all five projects (`LARGA.Shared.Models`, `LARGA.MobileApp`, `LARGA.SharedCore`, `LARGA.SeedTool`, `LARGA.ManagerWeb`) must restore and build successfully.
2. Manually test the affected client (`MobileApp` and/or `ManagerWeb`) against a **real Firebase development project/configuration** — there is no local emulator/mock backend documented for full end-to-end testing.
3. Confirm no credential files, `bin/`, or `obj/` artifacts are staged for commit.

## 4. Testing Strategy

- **Unit tests for boundary/business logic**: Core computations that don't depend on device hardware or Firebase connectivity — e.g., boundary rate calculations, late-penalty application, running debt-balance aggregation (`business-rules.md` §4) — should be covered by unit tests against `LARGA.SharedCore` services, ideally in isolation from live Firestore calls.
- **Mock dependency injection for device sensors and platform services**: Mobile-only concerns (OCR via `IOcrService`, GPS/telemetry capture, future accelerometer/shake-sensor SOS triggers) should be tested by mocking the relevant interface (e.g., `IOcrService`) rather than depending on real hardware, so that ViewModel and service logic can be exercised in a standard unit-test host.
- **No automated test project currently exists in the solution** (`get_projects_in_solution` returns only `LARGA.Shared.Models`, `LARGA.MobileApp`, `LARGA.SharedCore`, `LARGA.SeedTool`, `LARGA.ManagerWeb`). Introducing a dedicated `LARGA.Tests` (xUnit or MSTest) project is the recommended next step to operationalize the strategy above — this is `[PENDING IMPLEMENTATION]`.
- Until a formal test project exists, the practical verification workflow is the manual checklist in §3 combined with `dotnet build` for compile-time safety.

## 5. Recommended Contribution Workflow

1. Branch from `develop`: `git checkout -b feature/<short-description> develop`.
2. Implement changes, keeping shared data-shape changes in `LARGA.Shared.Models` first so both clients compile against the same contract (per `README.md`).
3. Build the full solution and run any available tests.
4. Manually verify the affected client against a Firebase dev environment.
5. Open a PR into `develop` following Conventional Commits, and address review feedback before merge.
6. Release branches are cut from `develop` when a stable set of features is ready; hotfixes branch from `main` when a production issue must be patched immediately, then are merged back into both `main` and `develop`.
