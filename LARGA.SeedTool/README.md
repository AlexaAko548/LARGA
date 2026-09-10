# LARGA.SeedTool

A console tool that seeds the `larga-blmtaxi` Firestore project with a small, internally-consistent
set of test data (5 drivers, 1 manager, 5 taxis, 11 shifts, boundary payments, fuel logs,
maintenance records + parts, spare parts inventory, emergency alerts, GPS telemetry, audit logs,
handover checklists, and upcoming shift schedules) so the ManagerWeb dashboard and its
not-yet-built pages (Garage, Inventory, Fuel Verification, Audit Logs) have something realistic
to render.

Every document uses a fixed ID, so **re-running the tool overwrites the same records** instead of
creating duplicates — safe to re-run to reset test data before a demo.

It also corrects two bad fields found in existing hand-entered data:
- `maintenance_logs`: `supportingPhotoURL` → `supportingPhotoUrl` (the code expects that exact casing)
- `boundary_payments`: `amountPaid` `"600"` (string) → `600` (number)

### Demonstrating all 5 live fleet-status states

[FleetReportingService](../LARGA.SharedCore/Services/FleetReportingService.cs) computes each taxi's
live status (see `docs/ERD.md` → "Live fleet status") rather than reading it from a stored field.
This tool seeds one dedicated, unambiguous example of each state:

| Taxi | Status | Why |
|---|---|---|
| `TAXI_001` | **Active** | Has a live shift with recent, moving GPS telemetry. |
| `TAXI_002` | **On Break** | Has a live shift with `isOnBreak = true`. |
| `TAXI_003` | **Maintenance** | `status = "Under Maintenance"` (an open engine repair). |
| `TAXI_004` | **SOS** | Has a live shift with an unresolved `emergency_alerts` doc. |
| `TAXI_005` | **Idle** | No live shift at all (Carlos hasn't started his first shift yet). |

## 1. Get a service account key

You need a Firebase/Google Cloud service account key with Firestore write access:

1. Go to the [Firebase Console](https://console.firebase.google.com/) → your `larga-blmtaxi` project → ⚙️ **Project settings** → **Service accounts**.
2. Click **Generate new private key**. This downloads a `.json` file.
3. Save it **outside the repo** (e.g. your Desktop or a local secrets folder) — never commit it. If you do save it inside the repo folder, the root `.gitignore` has a catch-all for common key filename patterns, but outside the repo is safer.

## 2. Run it

From the repo root:

```
dotnet run --project LARGA.SeedTool -- "C:\path\to\your-service-account-key.json"
```

It will print a summary of what it's about to write and ask you to type `YES` to continue.

If you've instead authenticated via `gcloud auth application-default login`, you can omit the key
path:

```
dotnet run --project LARGA.SeedTool
```

To target a different project id, pass it as the second argument:

```
dotnet run --project LARGA.SeedTool -- "C:\path\to\key.json" some-other-project-id
```

## What it does NOT touch

- `chats/*` — left alone; this is already populated by the real driver↔manager messaging flow.
- `users/WD2nFT8xngMPNYQb0xEeIyPAenl1` — the real Firebase-Auth-linked manager account. Not modified.
- Anything not listed above (e.g. it won't delete collections or documents it doesn't know about).
