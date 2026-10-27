# Firestore rules tests

Automated tests for `firestore.rules` (Section A of the security audit), using `@firebase/rules-unit-testing`.
They run **only on the local Firebase emulator**, with the demo project ID `demo-larga-rules`. A `demo-` project ID can't reach any real Firebase
project, so nothing here touches production.

## Run

Needs Node 18+ and Java 11+ (the Firestore emulator is a Java program).

```bash
cd tests/rules
npm install
npm test
```

`npm test` starts the Firestore and Storage emulators, runs `firestore.test.js` (against `../../firestore.rules`) and
`storage.test.js` (against `../../storage.rules`, which reads roles from Firestore), then stops the emulators.
The first run downloads the emulator (about 60 MB) into your user profile.

## Reading the results

Each test describes the **secure** behaviour. Tests named `[GAP X-nn]` cover a hole listed in `SECURITY_AUDIT.md`. They fail until that fix
is applied. Any failure **without** `[GAP]` means a rules change broke something that used to be protected.

The `PERMISSION_DENIED` lines in the output are expected. They are the denied writes the tests are checking for.

## Results

| Date | Rules version | Passing | Failing | Notes |
|---|---|---|---|---|
| 2026-10-19 | `0a2a5bd` (before Part 2 fixes) | 53 | 14 | All 14 failures are `[GAP]` tests: A-01 ×2, A-02 ×3, A-03 ×2, A-04, A-05 ×2, A-06 ×2, B-03, C-03 |
| 2026-10-19 | `e3eb5f9` (after Part 2 fixes) | Firestore 80, Storage 22 | 0 | Added tests for the app's real clock-in, clock-out and fuel writes, plus `storage.test.js` |
| 2026-10-19 | `ddfdd0e` (repo rules) | Firestore 87, Storage 35 | 0 | Retention, open-shift query and legacy-folder tests added |
| 2026-10-19 | **live console rules** (pasted) | Firestore 63, Storage 10 | Firestore 24, Storage 25 | What production allows today; see SECURITY_AUDIT.md "Live console rules vs repo" |


| 2026-10-24 | `fix/emergency-sos-alpha-defects` (repo rules) | Firestore 107, Storage 35 | 0 | M7 SOS: emergency_alerts block (own active shift only, Manager-only resolve, no deletes, no server-owned fields from the driver) |
