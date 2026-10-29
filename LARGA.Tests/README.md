# LARGA.Tests

Automated tests for LARGA (BLM Taxi), written with xUnit on .NET 9. Most of them test the shared logic in `LARGA.SharedCore`.
The maintenance tests also call a few helpers on ManagerWeb's Garage page. The mobile app (MAUI) can't be loaded by a test project, so
anything that only lives there is tested on the emulator instead (see "Alpha test cases" below).

## Layout

| Folder | Module | What's in it |
|---|---|---|
| `Fuel/` | M3 Fuel & OCR | Receipt parsing, OCR robustness, real ML Kit receipts, fuel review numbers |
| `Maintenance/` | M4 Vehicle Maintenance | Priority and date rules, the Garage page's due/overdue text, Garage work-order lifecycle and routine-check alerts, spare-part stock status and archiving, a work order's parts deducted all-or-nothing, low-stock alerts |
| `Gps/` | M6 GPS Tracking | Idle detection, the GPS jitter filter, idle alerts |
| `Sos/` | M7 SOS Emergency | Hostile/Crash detectors fed recorded sensor traces, SOS protocol rules |
| `Live/` | – | Helpers that talk to the **live** project. They do nothing unless their env vars are set (see below) |
| `Support/` | – | Shared test helpers (`Emulator.cs`: local Firestore emulator setup) |
| `Rules/` | M4 / M6 | `firestore.rules` tests for `gps_telemetry` and `maintenance_logs` (JavaScript) |
| `Fixtures/Fuel/` | M3 | ML Kit text blocks captured from real receipts on the emulator |
| `Fixtures/Sos/` | M7 | Sensor traces: `sos*.txt` are generated, `emu_*.txt` were recorded on the emulator |

Namespaces follow the folders (`LARGA.Tests.Fuel`, `LARGA.Tests.Sos`, ...).

## Run

Needs the .NET 9 SDK.

```bash
dotnet test LARGA.Tests/LARGA.Tests.csproj
```

One module only:

```bash
dotnet test LARGA.Tests/LARGA.Tests.csproj --filter "FullyQualifiedName~LARGA.Tests.Sos"
```

A plain run skips the emulator tests and does nothing in `Live/`, so it never needs a network or credentials.

### Tests that fail on purpose

Some tests pin a defect that hasn't been fixed yet. None of the plain-run tests do right now; any that do are listed here and fail until the fix lands.

With the emulator running, some do fail: MNT-015/016 (alert timing), BRP-014's `Brp014_Configured900_WithMalformedUnrelatedField_StillUsed`
(a value of the wrong type in an unrelated `system_configs/global` field makes the web ledger fall back to the ₱800 default instead of
the configured ₱900), and the `Diag_*` tests that pin those causes (`Diag_BadUnrelatedConfigField_StillHonoursConfiguredWeeklyInterval`,
`Diag_ProxyWithoutFirestoreDataAttribute_ConvertTo`). GPS-014 itself passes now.

## Emulator tests (Garage, inventory, idle detection)

These run `GarageService`, `InventoryAuditService`, `FleetReportingService` and `AlertService` against a **local** Firestore emulator. They use the demo project ID
`demo-larga-tests`, which can't reach a real Firebase project. They're skipped unless `LARGA_EMULATOR=1` is set.

Needs Node 18+ and Java 11+. Start the emulator first, using the copy of firebase-tools that `tests/rules` installs:

```bash
cd tests/rules && npm install && ./node_modules/.bin/firebase emulators:start --only firestore --project demo-larga-tests
```

Then, in another terminal:

```bash
LARGA_EMULATOR=1 dotnet test LARGA.Tests/LARGA.Tests.csproj
```

## Rules tests (JavaScript)

`Rules/gps_mnt.rules.test.js` checks the repo's `firestore.rules` for GPS-004/005 and driver defect reports (MNT-002/003). With the
emulator above running:

```bash
FIRESTORE_EMULATOR_HOST=127.0.0.1:8080 NODE_PATH=tests/rules/node_modules tests/rules/node_modules/.bin/mocha --timeout 20000 LARGA.Tests/Rules/gps_mnt.rules.test.js
```

The `PERMISSION_DENIED` lines in the output are expected: they're the writes the tests check get refused. The full rules suite lives in
`tests/rules` (see its README).

## Live helpers (`Live/`) – use with care

These use the Firebase **admin key** against the real `larga-blmtaxi` project, which bypasses the security rules. Never commit the key.
Point to it with `LARGA_CREDENTIALS`.

| Helper | Env vars | What it does |
|---|---|---|
| `LiveReadOnlyProbe` | `LARGA_LIVE_READONLY=1`, `LARGA_CREDENTIALS` (optional `LARGA_POINTS=1`) | **Read-only.** Prints `system_configs/global`, the GPS TTL policy, recent shifts and their GPS points, recent defect reports and routine checks |
| `LiveRemoteClose` | `LARGA_REMOTE_CLOSE_SHIFT`, `LARGA_REMOTE_CLOSE_DRIVER`, `LARGA_CREDENTIALS` | **Writes.** Closes one named *Active* test shift the way the 6:00 AM auto-close does, then releases its locks. Only for GPS-006 testing, on a shift you started yourself |

```bash
LARGA_LIVE_READONLY=1 LARGA_CREDENTIALS='C:\path\to\admin-key.json' dotnet test LARGA.Tests/LARGA.Tests.csproj --filter "FullyQualifiedName~LiveReadOnlyProbe" --logger "console;verbosity=detailed"
```

## Fixtures

- `Fixtures/Sos/sos*.txt` are generated: `python LARGA.Tests/Fixtures/Sos/gen_sos_traces.py` rewrites them next to the script. The format
  is described in `LARGA.SharedCore/Emergency/SensorTrace.cs`.
- `Fixtures/Sos/emu_*.txt` and `Fixtures/Fuel/*.txt` were captured on the emulator (DEBUG record hooks / `LARGA-OCR` logcat lines). They
  can't be regenerated; re-record them if the capture format changes.

Every `.txt` under `Fixtures/` is copied to the build output with its subfolder, so tests read them from
`Fixtures/<Module>/<file>` next to the test DLL.

## Alpha test cases

The test IDs in method names (`Mnt013_...`, `Gps014_...`, `Fuel016_...`, `SOS_008_...`) match the IDs in the LARGA test-case workbook.
Cases that need a real phone (camera, swipe-away, offline driving, the manager map) are run by hand on the Android emulator, not here.
