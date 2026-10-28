// Firestore security rules tests for LARGA (Section A of the pre-deployment audit).
//
// Runs ONLY against the local Firestore emulator (project "demo-larga-rules" - a demo- project ID can never reach a
// real Firebase project). Run with:  cd tests/rules && npm test
//
// Every test states the SECURE behaviour. Tests whose name starts with "[GAP]" describe a hole found in the audit:
// they FAIL against the current firestore.rules and should pass once the matching SECURITY_AUDIT.md fix is applied.

const fs = require('fs');
const path = require('path');
const {
  initializeTestEnvironment,
  assertSucceeds,
  assertFails,
} = require('@firebase/rules-unit-testing');
const {
  doc, getDoc, setDoc, updateDoc, deleteDoc, addDoc, collection, getDocs, query, where,
  serverTimestamp, Timestamp,
} = require('firebase/firestore');

const PROJECT_ID = 'demo-larga-rules';
const RULES = fs.readFileSync(path.join(__dirname, '..', '..', 'firestore.rules'), 'utf8');

const MANAGER = 'mgr1';
const ASSISTANT = 'asst1';
const DRIVER_A = 'drvA';
const DRIVER_B = 'drvB';
const STRANGER = 'stranger'; // signed in to Firebase Auth, but has no users/{uid} profile

let env;

const as = (uid) => env.authenticatedContext(uid).firestore();
const anon = () => env.unauthenticatedContext().firestore();
const hoursAgo = (h) => Timestamp.fromDate(new Date(Date.now() - h * 3600 * 1000));

async function seed(extra) {
  await env.withSecurityRulesDisabled(async (ctx) => {
    const db = ctx.firestore();
    const old = Timestamp.fromDate(new Date('2026-10-01T00:00:00Z'));
    await setDoc(doc(db, 'users', MANAGER), { role: 'Manager', fullName: 'Mara Manager' });
    await setDoc(doc(db, 'users', ASSISTANT), { role: 'Assistant Manager', fullName: 'Ana Assistant' });
    await setDoc(doc(db, 'users', DRIVER_A), { role: 'Driver', fullName: 'Dan A', assignedTaxiId: 'TX-01', phoneNumber: '09170000001' });
    await setDoc(doc(db, 'users', DRIVER_B), { role: 'Driver', fullName: 'Ben B', assignedTaxiId: 'TX-02', phoneNumber: '09170000002' });
    await setDoc(doc(db, 'taxis', 'TX-01'), { taxiId: 'TX-01', plateNumber: 'ABC 101', status: 'Active Unit' });
    await setDoc(doc(db, 'taxis', 'TX-02'), { taxiId: 'TX-02', plateNumber: 'ABC 102', status: 'Under Maintenance' });
    await setDoc(doc(db, 'system_configs', 'global'), { defaultBoundaryRate: 1200, testClockPh: '' });
    await setDoc(doc(db, 'shifts', 'shiftA'), { shiftId: 'shiftA', driverId: DRIVER_A, taxiId: 'TX-01', status: 'Active', shiftStart: old, startMileage: 1000 });
    await setDoc(doc(db, 'shifts', 'shiftA_old'), { shiftId: 'shiftA_old', driverId: DRIVER_A, taxiId: 'TX-01', status: 'Completed', shiftStart: old, shiftEnd: old });
    await setDoc(doc(db, 'shifts', 'shiftB'), { shiftId: 'shiftB', driverId: DRIVER_B, taxiId: 'TX-02', status: 'Active', shiftStart: old });
    await setDoc(doc(db, 'shift_locks', 'driver_' + DRIVER_B), { driverId: DRIVER_B, shiftId: 'shiftB', shiftStart: old, released: false });
    await setDoc(doc(db, 'boundary_payments', 'payB'), { shiftId: 'shiftB', driverId: DRIVER_B, amountPaid: 1200, paymentStatus: 'Paid' });
    await setDoc(doc(db, 'debt_adjustments', 'adjB'), { driverId: DRIVER_B, amount: 500, reason: 'penalty' });
    await setDoc(doc(db, 'fuel_logs', 'fuelB'), { driverId: DRIVER_B, shiftId: 'shiftB', verificationStatus: 'Pending' });
    await setDoc(doc(db, 'gps_telemetry', 'gpsB'), { driverId: DRIVER_B, shiftId: 'shiftB', latitude: 14.6, longitude: 121.0 });
    await setDoc(doc(db, 'audit_logs', 'log1'), { userId: MANAGER, actionType: 'Seed', timestamp: old });
    await setDoc(doc(db, 'payment_references', 'REF123'), { paymentId: 'payB', driverId: DRIVER_B });
    if (extra) await extra(db);
  });
}

before(async () => {
  env = await initializeTestEnvironment({ projectId: PROJECT_ID, firestore: { rules: RULES } });
});

beforeEach(async () => {
  await env.clearFirestore();
  await seed();
});

after(async () => {
  await env.cleanup();
});

describe('Unauthenticated user', () => {
  for (const p of ['users/drvA', 'shifts/shiftA', 'boundary_payments/payB', 'taxis/TX-01', 'system_configs/global',
                   'gps_telemetry/gpsB', 'audit_logs/log1', 'debt_adjustments/adjB']) {
    it(`cannot read ${p}`, async () => assertFails(getDoc(doc(anon(), p))));
  }
  it('cannot create a shift', async () =>
    assertFails(setDoc(doc(anon(), 'shifts', 'x'), { driverId: DRIVER_A, status: 'Active', shiftStart: serverTimestamp() })));
  it('cannot raise an SOS', async () =>
    assertFails(addDoc(collection(anon(), 'emergency_alerts'), { driverId: DRIVER_A, shiftId: 'shiftA' })));
});

describe('Signed-in account with no LARGA profile (anyone can sign up with the public API key)', () => {
  it('cannot make itself a Manager (users/{self} with role Manager)', async () =>
    assertFails(setDoc(doc(as(STRANGER), 'users', STRANGER), { role: 'Manager', fullName: 'Eve' })));
  it('cannot read drivers', async () => assertFails(getDoc(doc(as(STRANGER), 'users', DRIVER_A))));
  it('[GAP A-02] cannot read the taxi list', async () => assertFails(getDocs(collection(as(STRANGER), 'taxis'))));
  it('[GAP A-02] cannot read system_configs/global', async () => assertFails(getDoc(doc(as(STRANGER), 'system_configs', 'global'))));
  it('[GAP A-02] cannot read shift_locks (driver IDs and shift start times)', async () =>
    assertFails(getDocs(collection(as(STRANGER), 'shift_locks'))));
});

describe('LARGA users still read the shared lists', () => {
  it('driver can read taxis', async () => assertSucceeds(getDocs(collection(as(DRIVER_A), 'taxis'))));
  it('driver can read system_configs/global', async () => assertSucceeds(getDoc(doc(as(DRIVER_A), 'system_configs', 'global'))));
  it("driver can read shift_locks (clock-in checks another driver's lock)", async () =>
    assertSucceeds(getDoc(doc(as(DRIVER_A), 'shift_locks', 'driver_' + DRIVER_B))));
  it('assistant manager can read taxis', async () => assertSucceeds(getDocs(collection(as(ASSISTANT), 'taxis'))));
});

describe('Driver A vs Driver B', () => {
  it("cannot read B's shift", async () => assertFails(getDoc(doc(as(DRIVER_A), 'shifts', 'shiftB'))));
  it('cannot list all shifts', async () => assertFails(getDocs(collection(as(DRIVER_A), 'shifts'))));
  it("can list their own open shifts (driver dashboard's query)", async () =>
    assertSucceeds(getDocs(query(collection(as(DRIVER_A), 'shifts'),
      where('driverId', '==', DRIVER_A), where('status', 'in', ['Active', 'Auto-Closed'])))));
  it('can list only their own shifts', async () =>
    assertSucceeds(getDocs(query(collection(as(DRIVER_A), 'shifts'), where('driverId', '==', DRIVER_A)))));
  it("cannot read B's payment", async () => assertFails(getDoc(doc(as(DRIVER_A), 'boundary_payments', 'payB'))));
  it("cannot read B's debt adjustment", async () => assertFails(getDoc(doc(as(DRIVER_A), 'debt_adjustments', 'adjB'))));
  it("cannot read B's fuel log", async () => assertFails(getDoc(doc(as(DRIVER_A), 'fuel_logs', 'fuelB'))));
  it('cannot read any GPS telemetry', async () => assertFails(getDoc(doc(as(DRIVER_A), 'gps_telemetry', 'gpsB'))));
  it("cannot move B's live position", async () =>
    assertFails(updateDoc(doc(as(DRIVER_A), 'shifts', 'shiftB'), { currentLatitude: 1, currentLongitude: 1 })));
  it("cannot write a GPS point for B's shift", async () =>
    assertFails(addDoc(collection(as(DRIVER_A), 'gps_telemetry'), { driverId: DRIVER_A, shiftId: 'shiftB', latitude: 1, longitude: 1 })));
  it('cannot submit a fuel log as B', async () =>
    assertFails(addDoc(collection(as(DRIVER_A), 'fuel_logs'), {
      driverId: DRIVER_B, shiftId: 'shiftB', verificationStatus: 'Pending', submittedAt: serverTimestamp() })));
  it("cannot change B's phone number", async () =>
    assertFails(updateDoc(doc(as(DRIVER_A), 'users', DRIVER_B), { phoneNumber: '0999' })));
});

// Philippine hour now: the clock-in rule only accepts 6:00 AM - 11:59 PM PH time.
const phHourNow = () => (new Date().getUTCHours() + 8) % 24;

// Exactly what ShiftManagementService.ClockInAsync writes.
const appClockIn = (id) => ({
  driverId: DRIVER_A, taxiId: 'TX-01', shiftStart: serverTimestamp(), startMileage: 1200,
  status: 'Active', shiftId: id, isOnBreak: false, managerNote: '',
});

describe('Driver tampering with their own money and records', () => {
  it('can clock in with exactly the fields the app writes', async function () {
    if (phHourNow() < 6) this.skip(); // before 6:00 AM PH the rule refuses every clock-in
    await assertSucceeds(setDoc(doc(as(DRIVER_A), 'shifts', 'newShift'), appClockIn('newShift')));
  });
  it('cannot change their own role', async () => assertFails(updateDoc(doc(as(DRIVER_A), 'users', DRIVER_A), { role: 'Manager' })));
  it('cannot change their own unit assignment', async () =>
    assertFails(updateDoc(doc(as(DRIVER_A), 'users', DRIVER_A), { assignedTaxiId: 'TX-02' })));
  it('cannot change their own arrears', async () =>
    assertFails(updateDoc(doc(as(DRIVER_A), 'users', DRIVER_A), { currentArrears: 0 })));
  it('cannot set a late fee on their shift', async () =>
    assertFails(updateDoc(doc(as(DRIVER_A), 'shifts', 'shiftA'), { lateFee: 0 })));
  it('cannot change their shift start time', async () =>
    assertFails(updateDoc(doc(as(DRIVER_A), 'shifts', 'shiftA'), { shiftStart: Timestamp.now() })));
  it('cannot set a negative fuel penalty at clock-out', async () =>
    assertFails(updateDoc(doc(as(DRIVER_A), 'shifts', 'shiftA'), { fuelPenalty: -500 })));
  it('[GAP A-01] cannot clock in with a negative fuel penalty (lowers their boundary)', async () =>
    assertFails(setDoc(doc(as(DRIVER_A), 'shifts', 'newShift'), { ...appClockIn('newShift'), fuelPenalty: -1000 })));
  it('[GAP A-01] cannot clock in with a start odometer of 0 (skips the final-odometer check)', async () =>
    assertFails(setDoc(doc(as(DRIVER_A), 'shifts', 'newShift'), { ...appClockIn('newShift'), startMileage: 0 })));
  it('[GAP A-01] cannot clock in with an end odometer already set', async () =>
    assertFails(setDoc(doc(as(DRIVER_A), 'shifts', 'newShift'), { ...appClockIn('newShift'), endMileage: 5 })));
  it('[GAP A-04] cannot write the manager note on their own shift', async () =>
    assertFails(updateDoc(doc(as(DRIVER_A), 'shifts', 'shiftA'), { managerNote: 'approved by manager' })));
  it('cannot record or edit a payment', async () =>
    assertFails(setDoc(doc(as(DRIVER_A), 'boundary_payments', 'fake'), { driverId: DRIVER_A, amountPaid: 99999, paymentStatus: 'Paid' })));
  it('cannot add a debt credit', async () =>
    assertFails(addDoc(collection(as(DRIVER_A), 'debt_adjustments'), { driverId: DRIVER_A, amount: -5000 })));
  it('cannot change system_configs', async () =>
    assertFails(updateDoc(doc(as(DRIVER_A), 'system_configs', 'global'), { defaultBoundaryRate: 1 })));
  it("[GAP A-03] cannot change another unit's status", async () =>
    assertFails(updateDoc(doc(as(DRIVER_A), 'taxis', 'TX-02'), { status: 'Active Unit' })));
  it('manager can change a unit status', async () =>
    assertSucceeds(updateDoc(doc(as(MANAGER), 'taxis', 'TX-02'), { status: 'Active Unit' })));
  it('[GAP A-03] cannot take their own unit out of "Under Maintenance"', async () => {
    await env.withSecurityRulesDisabled((ctx) => updateDoc(doc(ctx.firestore(), 'taxis', 'TX-01'), { status: 'Under Maintenance' }));
    await assertFails(updateDoc(doc(as(DRIVER_A), 'taxis', 'TX-01'), { status: 'Active Unit' }));
  });
  it('cannot delete a shift', async () => assertFails(deleteDoc(doc(as(DRIVER_A), 'shifts', 'shiftA'))));
  it('can clock out with exactly the fields the app writes', async () =>
    assertSucceeds(updateDoc(doc(as(DRIVER_A), 'shifts', 'shiftA'), {
      shiftEnd: serverTimestamp(), endMileage: 1300, status: 'Completed', managerNote: '', isOnBreak: false, fuelPenalty: 50 })));
  it('can submit a fuel receipt for their own shift', async () =>
    assertSucceeds(addDoc(collection(as(DRIVER_A), 'fuel_logs'), {
      driverId: DRIVER_A, shiftId: 'shiftA', verificationStatus: 'Pending', submittedAt: serverTimestamp(),
      litersRefueled: 30.5, fuelCost: 1950 })));
  it('cannot submit a fuel receipt with the old "UNKNOWN_SHIFT" placeholder (REQ-4.3-3)', async () =>
    assertFails(addDoc(collection(as(DRIVER_A), 'fuel_logs'), {
      driverId: DRIVER_A, shiftId: 'UNKNOWN_SHIFT', verificationStatus: 'Pending', submittedAt: serverTimestamp(),
      litersRefueled: 30.5, fuelCost: 1950 })));
  it('[GAP D-03] cannot submit a fuel receipt with a negative cost', async () =>
    assertFails(addDoc(collection(as(DRIVER_A), 'fuel_logs'), {
      driverId: DRIVER_A, shiftId: 'shiftA', verificationStatus: 'Pending', submittedAt: serverTimestamp(),
      litersRefueled: 30, fuelCost: -1950 })));
  it("[GAP D-03] cannot submit a fuel receipt against B's shift", async () =>
    assertFails(addDoc(collection(as(DRIVER_A), 'fuel_logs'), {
      driverId: DRIVER_A, shiftId: 'shiftB', verificationStatus: 'Pending', submittedAt: serverTimestamp(),
      litersRefueled: 30, fuelCost: 1950 })));
  it('cannot backdate a new shift while the test clock is off', async () =>
    assertFails(setDoc(doc(as(DRIVER_A), 'shifts', 'newShift'), {
      ...appClockIn('newShift'), shiftStart: Timestamp.fromDate(new Date('2026-10-01T00:30:00Z')) })));
  it('[GAP B-03] cannot backdate a new shift even when a manager left testClockPh set', async () => {
    await env.withSecurityRulesDisabled((ctx) => updateDoc(doc(ctx.firestore(), 'system_configs', 'global'), { testClockPh: '2026-10-01 07:00' }));
    await assertFails(setDoc(doc(as(DRIVER_A), 'shifts', 'newShift'), {
      ...appClockIn('newShift'), shiftStart: Timestamp.fromDate(new Date('2026-09-01T00:30:00Z')) }));
  });
  it('[GAP C-03] cannot raise an SOS on a shift that already ended', async () =>
    assertFails(addDoc(collection(as(DRIVER_A), 'emergency_alerts'), { driverId: DRIVER_A, shiftId: 'shiftA_old', isResolved: false })));
  it('can raise an SOS on their own active shift', async () =>
    assertSucceeds(addDoc(collection(as(DRIVER_A), 'emergency_alerts'), { driverId: DRIVER_A, shiftId: 'shiftA', isResolved: false })));
  it("cannot raise an SOS on B's shift", async () =>
    assertFails(addDoc(collection(as(DRIVER_A), 'emergency_alerts'), { driverId: DRIVER_A, shiftId: 'shiftB' })));
});

describe('SOS emergency_alerts (M7 SOS-002/006/007)', () => {
  // The payload EmergencyAlertService writes (manual button, Hostile and Crash protocols).
  const appSos = (over) => ({
    shiftId: 'shiftA', driverId: DRIVER_A, driverName: 'Dan A', taxiUnit: 'ABC · 101',
    latitude: 10.3157, longitude: 123.8854, isResolved: false, timestamp: new Date(),
    triggerType: 'Hostile', locationSource: 'live', taxiId: 'TX-01',
    callerPhone: '+639170000001', devicePhone: '', ...over,
  });
  const seedAlert = () => env.withSecurityRulesDisabled((ctx) =>
    setDoc(doc(ctx.firestore(), 'emergency_alerts', 'sos1'), appSos({ triggerType: 'Crash' })));

  it('SOS-002/006 driver can raise the app\'s full SOS on their own active shift', async () =>
    assertSucceeds(addDoc(collection(as(DRIVER_A), 'emergency_alerts'), appSos())));
  it('SOS-003 an SOS without a location (0/0, locationSource none) is accepted', async () =>
    assertSucceeds(addDoc(collection(as(DRIVER_A), 'emergency_alerts'), appSos({ latitude: 0, longitude: 0, locationSource: 'none' }))));
  it('SOS-002 driver cannot raise an SOS for a shift that does not exist', async () =>
    assertFails(addDoc(collection(as(DRIVER_A), 'emergency_alerts'), appSos({ shiftId: 'noSuchShift' }))));
  it('SOS-002 driver cannot raise an SOS on their completed shift', async () =>
    assertFails(addDoc(collection(as(DRIVER_A), 'emergency_alerts'), appSos({ shiftId: 'shiftA_old' }))));
  it("SOS-006 driver cannot raise an SOS on another driver's active shift", async () =>
    assertFails(addDoc(collection(as(DRIVER_A), 'emergency_alerts'), appSos({ shiftId: 'shiftB' }))));
  it('SOS-006 driver cannot raise an SOS in another driver\'s name', async () =>
    assertFails(addDoc(collection(as(DRIVER_A), 'emergency_alerts'), appSos({ driverId: DRIVER_B, shiftId: 'shiftB' }))));
  it('SOS-006 staff cannot raise a driver SOS', async () => {
    await assertFails(addDoc(collection(as(MANAGER), 'emergency_alerts'), appSos()));
    await assertFails(addDoc(collection(as(ASSISTANT), 'emergency_alerts'), appSos()));
  });
  for (const [field, value] of [['pushSentAt', serverTimestamp()], ['callerCheck', 'Verified'], ['callerVerified', true],
                                ['callerVerificationReason', 'ok'], ['address', 'Somewhere'], ['addressSource', 'maptiler'],
                                ['resolvedAt', serverTimestamp()], ['isResolved', true]]) {
    it(`SOS-006 driver cannot pre-set the server-owned ${field} on an SOS`, async () =>
      assertFails(addDoc(collection(as(DRIVER_A), 'emergency_alerts'), appSos({ [field]: value }))));
  }
  it('manager and assistant manager can read alerts; drivers cannot', async () => {
    await seedAlert();
    await assertSucceeds(getDoc(doc(as(MANAGER), 'emergency_alerts', 'sos1')));
    await assertSucceeds(getDoc(doc(as(ASSISTANT), 'emergency_alerts', 'sos1')));
    await assertFails(getDoc(doc(as(DRIVER_A), 'emergency_alerts', 'sos1')));
  });
  it('SOS-007 manager can resolve an SOS', async () => {
    await seedAlert();
    await assertSucceeds(updateDoc(doc(as(MANAGER), 'emergency_alerts', 'sos1'), { isResolved: true, resolvedAt: serverTimestamp() }));
  });
  it('SOS-007 assistant manager cannot resolve an SOS', async () => {
    await seedAlert();
    await assertFails(updateDoc(doc(as(ASSISTANT), 'emergency_alerts', 'sos1'), { isResolved: true, resolvedAt: serverTimestamp() }));
  });
  it('SOS-007 driver cannot resolve their own SOS', async () => {
    await seedAlert();
    await assertFails(updateDoc(doc(as(DRIVER_A), 'emergency_alerts', 'sos1'), { isResolved: true }));
  });
  it('nobody can delete an SOS from the apps', async () => {
    await seedAlert();
    await assertFails(deleteDoc(doc(as(MANAGER), 'emergency_alerts', 'sos1')));
    await assertFails(deleteDoc(doc(as(DRIVER_A), 'emergency_alerts', 'sos1')));
  });
});

describe('Retention (expireAt, audit H-01)', () => {
  const days = (n) => Timestamp.fromDate(new Date(Date.now() + n * 86400000));
  const gps = (expireAt) => ({ driverId: DRIVER_A, shiftId: 'shiftA', latitude: 14.6, longitude: 121, expireAt });
  it('driver can write a GPS point that expires in 90 days', async () =>
    assertSucceeds(addDoc(collection(as(DRIVER_A), 'gps_telemetry'), gps(days(90)))));
  it('driver cannot write a GPS point that expires tomorrow (early deletion)', async () =>
    assertFails(addDoc(collection(as(DRIVER_A), 'gps_telemetry'), gps(days(1)))));
  it('driver cannot write a GPS point that never expires (10 years)', async () =>
    assertFails(addDoc(collection(as(DRIVER_A), 'gps_telemetry'), gps(days(3650)))));
  it('driver cannot set a retention date on an SOS', async () =>
    assertFails(addDoc(collection(as(DRIVER_A), 'emergency_alerts'), { driverId: DRIVER_A, shiftId: 'shiftA', expireAt: days(365) })));
  it('driver can send a chat message that expires in a year', async () =>
    assertSucceeds(addDoc(collection(as(DRIVER_A), 'chats', DRIVER_A, 'messages'), { text: 'hi', isDriver: true, expireAt: days(365) })));
  it('driver cannot send a chat message that expires today', async () =>
    assertFails(addDoc(collection(as(DRIVER_A), 'chats', DRIVER_A, 'messages'), { text: 'hi', isDriver: true, expireAt: days(0) })));
});

describe('Audit logs', () => {
  it('manager can add an entry as themselves', async () =>
    assertSucceeds(addDoc(collection(as(MANAGER), 'audit_logs'), { userId: MANAGER, actionType: 'X', timestamp: serverTimestamp() })));
  it('manager cannot edit an entry (gap #19)', async () =>
    assertFails(updateDoc(doc(as(MANAGER), 'audit_logs', 'log1'), { actionType: 'Changed' })));
  it('manager cannot delete an entry (gap #19)', async () =>
    assertFails(deleteDoc(doc(as(MANAGER), 'audit_logs', 'log1'))));
  it('[GAP A-05] manager cannot add an entry in someone else\'s name', async () =>
    assertFails(addDoc(collection(as(MANAGER), 'audit_logs'), { userId: DRIVER_B, actionType: 'X', timestamp: serverTimestamp() })));
  it('[GAP A-05] manager cannot backdate an entry', async () =>
    assertFails(addDoc(collection(as(MANAGER), 'audit_logs'), {
      userId: MANAGER, actionType: 'X', timestamp: Timestamp.fromDate(new Date('2020-01-01T00:00:00Z')) })));
  it('driver can log their own SOS', async () =>
    assertSucceeds(addDoc(collection(as(DRIVER_A), 'audit_logs'), { userId: DRIVER_A, actionType: 'SosTriggered', timestamp: serverTimestamp() })));
  it('[GAP A-05] driver cannot log their SOS with a phone-clock time', async () =>
    assertFails(addDoc(collection(as(DRIVER_A), 'audit_logs'), {
      userId: DRIVER_A, actionType: 'SosTriggered', timestamp: Timestamp.fromDate(new Date('2026-01-01T00:00:00Z')) })));
  it('driver cannot log other actions', async () =>
    assertFails(addDoc(collection(as(DRIVER_A), 'audit_logs'), { userId: DRIVER_A, actionType: 'PaymentRecorded' })));
  it('driver cannot read audit logs', async () => assertFails(getDoc(doc(as(DRIVER_A), 'audit_logs', 'log1'))));
  it('assistant manager cannot read audit logs', async () => assertFails(getDoc(doc(as(ASSISTANT), 'audit_logs', 'log1'))));
});

describe('system_configs/global', () => {
  it('manager can change it', async () =>
    assertSucceeds(updateDoc(doc(as(MANAGER), 'system_configs', 'global'), { defaultBoundaryRate: 1300 })));
  it('assistant manager cannot change it', async () =>
    assertFails(updateDoc(doc(as(ASSISTANT), 'system_configs', 'global'), { defaultBoundaryRate: 1 })));
  it('driver cannot change it', async () =>
    assertFails(updateDoc(doc(as(DRIVER_A), 'system_configs', 'global'), { testClockPh: 'x' })));
});

describe('Payments (staff)', () => {
  it('assistant manager can record a new payment', async () =>
    assertSucceeds(setDoc(doc(as(ASSISTANT), 'boundary_payments', 'newPay'), { driverId: DRIVER_A, shiftId: 'shiftA', amountPaid: 500 })));
  it('[GAP A-06] assistant manager cannot rewrite an existing payment amount', async () =>
    assertFails(updateDoc(doc(as(ASSISTANT), 'boundary_payments', 'payB'), { amountPaid: 1 })));
  it('[GAP A-06] payments cannot be negative', async () =>
    assertFails(setDoc(doc(as(MANAGER), 'boundary_payments', 'negPay'), { driverId: DRIVER_A, shiftId: 'shiftA', amountPaid: -500 })));
  it('assistant manager cannot delete a payment', async () =>
    assertFails(deleteDoc(doc(as(ASSISTANT), 'boundary_payments', 'payB'))));
  it('a GCash reference cannot be claimed twice (BRP-008)', async () =>
    assertFails(setDoc(doc(as(MANAGER), 'payment_references', 'REF123'), { paymentId: 'other', driverId: DRIVER_A })));
  it('assistant manager cannot add debt adjustments', async () =>
    assertFails(addDoc(collection(as(ASSISTANT), 'debt_adjustments'), { driverId: DRIVER_A, amount: -100 })));
});

describe('Alerts', () => {
  it('staff may only flip isRead on a system alert', async () => {
    await env.withSecurityRulesDisabled((ctx) => setDoc(doc(ctx.firestore(), 'system_alerts', 'a1'), { type: 'LowStock', isRead: false, message: 'm' }));
    await assertSucceeds(updateDoc(doc(as(ASSISTANT), 'system_alerts', 'a1'), { isRead: true }));
    await assertFails(updateDoc(doc(as(ASSISTANT), 'system_alerts', 'a1'), { message: 'edited' }));
  });
  it('driver cannot read system alerts', async () => {
    await env.withSecurityRulesDisabled((ctx) => setDoc(doc(ctx.firestore(), 'system_alerts', 'a1'), { type: 'LowStock', isRead: false }));
    await assertFails(getDoc(doc(as(DRIVER_A), 'system_alerts', 'a1')));
  });
  it('nobody can create a system alert from a client', async () =>
    assertFails(setDoc(doc(as(MANAGER), 'system_alerts', 'fake'), { type: 'SOS', isRead: false })));
});

describe('Fuel logs linked to a shift (REQ-4.3-3, REQ-5.5-6)', () => {
  const fuel = (shiftId) => ({
    driverId: DRIVER_A, shiftId, verificationStatus: 'Pending', submittedAt: serverTimestamp(), litersRefueled: 30, fuelCost: 1950,
  });
  async function seedShift(id, fields) {
    await env.withSecurityRulesDisabled((ctx) => setDoc(doc(ctx.firestore(), 'shifts', id),
      { shiftId: id, driverId: DRIVER_A, taxiId: 'TX-01', ...fields }));
  }

  it('can file against a shift that ended 2 hours ago', async () => {
    await seedShift('ended2h', { status: 'Completed', shiftStart: hoursAgo(14), shiftEnd: hoursAgo(2) });
    await assertSucceeds(addDoc(collection(as(DRIVER_A), 'fuel_logs'), fuel('ended2h')));
  });
  it('cannot file against a shift that ended 13 hours ago', async () => {
    await seedShift('ended13h', { status: 'Completed', shiftStart: hoursAgo(25), shiftEnd: hoursAgo(13) });
    await assertFails(addDoc(collection(as(DRIVER_A), 'fuel_logs'), fuel('ended13h')));
  });
  it('can file against a recent Auto-Closed shift (no shiftEnd)', async () => {
    await seedShift('auto', { status: 'Auto-Closed', shiftStart: hoursAgo(25) });
    await assertSucceeds(addDoc(collection(as(DRIVER_A), 'fuel_logs'), fuel('auto')));
  });
  it('cannot file against an old Auto-Closed shift', async () => {
    await seedShift('autoOld', { status: 'Auto-Closed', shiftStart: hoursAgo(40) });
    await assertFails(addDoc(collection(as(DRIVER_A), 'fuel_logs'), fuel('autoOld')));
  });
  it('cannot file against a shift that does not exist', async () =>
    assertFails(addDoc(collection(as(DRIVER_A), 'fuel_logs'), fuel('noSuchShift'))));
  it('cannot file with no shift at all', async () =>
    assertFails(addDoc(collection(as(DRIVER_A), 'fuel_logs'), fuel(''))));
  it("cannot file against B's recently ended shift", async () => {
    await env.withSecurityRulesDisabled((ctx) => setDoc(doc(ctx.firestore(), 'shifts', 'bEnded'),
      { driverId: DRIVER_B, status: 'Completed', shiftStart: hoursAgo(10), shiftEnd: hoursAgo(1) }));
    await assertFails(addDoc(collection(as(DRIVER_A), 'fuel_logs'), fuel('bEnded')));
  });
});
