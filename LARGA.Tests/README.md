# LARGA.Tests

Automated tests for LARGA (BLM Taxi), written with xUnit on .NET 9. Most of them test the shared logic in `LARGA.SharedCore`.
The maintenance tests also call a few helpers on ManagerWeb's Garage page. The mobile app (MAUI) can't be loaded by a test project, so
anything that only lives there is tested on the emulator instead (see "Alpha test cases" below).

## Layout

| Folder | Module | What's in it |
|---|---|---|
| `Fuel/` | M3 Fuel & OCR | Receipt parsing, OCR robustness, real ML Kit receipts and odometer photos, seven-segment decoder, OR-number rules, fuel-on-shift rules, fuel review numbers, OR-number claim backfill (emulator) |
| `Maintenance/` | M4 Vehicle Maintenance | Priority and date rules, the Garage page's due/overdue text, Garage work-order lifecycle and routine-check alerts, spare-part stock status and archiving, a work order's parts deducted all-or-nothing, low-stock alerts, pre-shift defect rules, day-off reminders |
| `Gps/` | M6 GPS Tracking | Idle detection, the GPS jitter filter, idle alerts, shift route, garage geofence circle |
| `Sos/` | M7 SOS Emergency | Crash detector fed recorded sensor traces, the Hostile volume-button pattern, SOS protocol rules, the SOS push `sentAt` stamp |
| `Alerts/` | Alert Center | Access-request and submission alerts, who sees which alert, alert links, the Unread count behind the Alerts tab badge |
| `Auth/` | Sign-in & accounts | Password policy, forced password change, staff roles, chat sender labels |
| `Chats/` | Messaging | Who a chat push goes to (`ChatPushPolicy`), per-person unread and the badge numbers (`ChatReadState`) |
| `Ledger/` | Payments & reports | Boundary payments (BRP-014 configured rate), ledger updates, report driver names (BETA-098), reading `system_configs/global` field by field, payment-reference claim backfill (BRP-008) |
| `Shifts/` | Clock-in | Pre-shift reminder rules, manager approve/deny of a clock-in request (emulator) |
| `Live/` | – | Helpers that talk to the **live** project. They do nothing unless their env vars are set (see below) |
| `Support/` | – | Shared test helpers (`Emulator.cs`: local Firestore emulator setup) |
| `Rules/` | M2 / M4 / M6 | `firestore.rules` tests (JavaScript): `gps_mnt.rules.test.js` for `gps_telemetry` and `maintenance_logs`, `brp.rules.test.js` for payments, shifts and debt adjustments |
| `Fixtures/Fuel/` | M3 | ML Kit text blocks captured from real receipts on the emulator |
| `Fixtures/Odometer/` | M3 | ML Kit text captured from the dashboard photos in `tests/odometer` (the photos are copied here at build time) |
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

Some tests pin a defect that hasn't been fixed yet. None do right now, with or without the emulator; any that do are listed here
and fail until the fix lands.

Fixed since: BRP-014 (a wrong-type value in an unrelated `system_configs/global` field put the web back on default settings, e.g.
the ₱800 boundary rate instead of the configured ₱900) - the web now reads that document field by field (`SystemConfigReader`).
MNT-015/016 were changed to the intended behaviour: a DueSoon alert goes out 2 days before a check is due (`maintenanceDueSoonDays`).

## Emulator tests

These run the web/shared services against a **local** Firestore emulator: `GarageService`, `InventoryAuditService`, `FleetReportingService`, `AlertService`,
`FinancialLedgerService`, `ClockInApprovalService`, `SystemConfigReader` and the one-time claim backfills (OR numbers, payment references). They use the demo project ID
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

Two files check the repo's `firestore.rules`: `Rules/gps_mnt.rules.test.js` (GPS-004/005 and driver defect reports MNT-002/003) and
`Rules/brp.rules.test.js` (BRP-026: drivers can't read other drivers' payments, shifts or debt adjustments, and ledger writes are staff-only).
With the emulator above running, from the repo root:

```bash
FIRESTORE_EMULATOR_HOST=127.0.0.1:8080 NODE_PATH=tests/rules/node_modules tests/rules/node_modules/.bin/mocha --timeout 20000 LARGA.Tests/Rules/gps_mnt.rules.test.js
```

```bash
FIRESTORE_EMULATOR_HOST=127.0.0.1:8080 NODE_PATH=tests/rules/node_modules tests/rules/node_modules/.bin/mocha --timeout 20000 LARGA.Tests/Rules/brp.rules.test.js
```

Run them one file at a time. Both share the same emulator project and clear it between tests, so running them in one mocha command can fail a test at random.

The `PERMISSION_DENIED` lines in the output are expected: they're the writes the tests check get refused. The full rules suite lives in
`tests/rules` (see its README).

## Live helpers (`Live/`) – use with care

These use the Firebase **admin key** against the real `larga-blmtaxi` project, which bypasses the security rules. Never commit the key.
Point to it with `LARGA_CREDENTIALS`.

| Helper | Env vars | What it does |
|---|---|---|
| `LiveReadOnlyProbe` | `LARGA_LIVE_READONLY=1`, `LARGA_CREDENTIALS` (optional `LARGA_POINTS=1`) | **Read-only.** Prints `system_configs/global`, the GPS TTL policy, recent shifts and their GPS points, recent defect reports and routine checks |
| `LiveLedgerReadOnlyProbe` | `LARGA_LIVE_READONLY=1`, `LARGA_CREDENTIALS` | **Read-only.** Prints the boundary rate as each reader sees it, and the web's Master Debt Ledger rows next to each driver's History total (M2 re-run) |
| `LiveRemoteClose` | `LARGA_REMOTE_CLOSE_SHIFT`, `LARGA_REMOTE_CLOSE_DRIVER`, `LARGA_CREDENTIALS` | **Writes.** Closes one named *Active* test shift the way the 6:00 AM auto-close does, then releases its locks. Only for GPS-006 testing, on a shift you started yourself |

```bash
LARGA_LIVE_READONLY=1 LARGA_CREDENTIALS='C:\path\to\admin-key.json' dotnet test LARGA.Tests/LARGA.Tests.csproj --filter "FullyQualifiedName~LiveReadOnlyProbe" --logger "console;verbosity=detailed"
```

## Fixtures

- `Fixtures/Sos/sos*.txt` are generated: `python LARGA.Tests/Fixtures/Sos/gen_sos_traces.py` rewrites them next to the script. The format
  is described in `LARGA.SharedCore/Emergency/SensorTrace.cs`.
- `Fixtures/Sos/emu_*.txt`, `Fixtures/Fuel/*.txt` and `Fixtures/Odometer/*.txt` were captured on the emulator (DEBUG record hooks / `LARGA-OCR` logcat lines). They
  can't be regenerated; re-record them if the capture format changes.

The Hostile protocol no longer has a motion trigger, so the SOS-008/010/011/012 motion cases and their traces were
removed; `sos_rough_road_120s.txt` stays as a Crash negative.

Every `.txt` under `Fixtures/` is copied to the build output with its subfolder, so tests read them from
`Fixtures/<Module>/<file>` next to the test DLL.

## Alpha test cases

The test IDs in method names (`Mnt013_...`, `Gps014_...`, `Fuel016_...`, `SOS_018_...`) match the IDs in the LARGA test-case workbook.
Cases that need a real phone (camera, swipe-away, offline driving, the manager map) are run by hand on the Android emulator, not here.

### Cases with no automated test

These workbook cases have no test here and are checked by hand: DSM-012 (web login messages), DSM-037 (a missed clock-out is auto-closed on the next
clock-in: needs a stale Active shift, so use the emulator or a dev project with `system_configs/global` `testClockPh`; the live project refuses it),
DSM-040 (double-tap on Confirm & Start Shift: one shift only) and DSM-041 (final odometer below the start is refused and the shift stays Active).
The SOS-008/010/011/012 motion cases were removed with the Hostile motion trigger.
