// M4/M6 alpha re-run: firestore.rules (repo copy) in the local emulator only.
// Run: NODE_PATH=tests/rules/node_modules tests/rules/node_modules/.bin/mocha --timeout 20000 LARGA.Tests/Rules/gps_mnt.rules.test.js
const fs = require('fs');
const path = require('path');
const { initializeTestEnvironment, assertSucceeds, assertFails } = require('@firebase/rules-unit-testing');
const { doc, setDoc, addDoc, collection, Timestamp } = require('firebase/firestore');

const PROJECT_ID = 'demo-larga-tests';
const RULES = fs.readFileSync(path.join(__dirname, '..', '..', 'firestore.rules'), 'utf8');
let env;
const as = (uid) => env.authenticatedContext(uid).firestore();
const days = (n) => Timestamp.fromDate(new Date(Date.now() + n * 86400000));

before(async () => {
  env = await initializeTestEnvironment({ projectId: PROJECT_ID, firestore: { rules: RULES, host: '127.0.0.1', port: 8080 } });
});
beforeEach(async () => {
  await env.clearFirestore();
  await env.withSecurityRulesDisabled(async (ctx) => {
    const db = ctx.firestore();
    await setDoc(doc(db, 'users', 'drv1'), { role: 'Driver', fullName: 'Driver One' });
    await setDoc(doc(db, 'users', 'drv2'), { role: 'Driver', fullName: 'Driver Two' });
    await setDoc(doc(db, 'shifts', 'active1'), { driverId: 'drv1', taxiId: 'TAXI_001', status: 'Active' });
    await setDoc(doc(db, 'shifts', 'done1'), { driverId: 'drv1', taxiId: 'TAXI_001', status: 'Completed' });
    await setDoc(doc(db, 'shifts', 'active2'), { driverId: 'drv2', taxiId: 'TAXI_002', status: 'Active' });
  });
});
after(async () => { await env.cleanup(); });

// Exactly what GpsTelemetryService writes.
const point = (shiftId, driverId = 'drv1') => ({
  shiftId, driverId, latitude: 10.25, longitude: 123.9, speed: 0,
  timestamp: Timestamp.now(), expireAt: days(90),
});

describe('M6 gps_telemetry rules', () => {
  it('baseline: driver1 may write a point for their own Active shift', () =>
    assertSucceeds(addDoc(collection(as('drv1'), 'gps_telemetry'), point('active1'))));
  it('GPS-004: refuses a point for an ended (Completed) shift', () =>
    assertFails(addDoc(collection(as('drv1'), 'gps_telemetry'), point('done1'))));
  it("GPS-005: refuses driver1 writing to driver2's Active shift (own driverId)", () =>
    assertFails(addDoc(collection(as('drv1'), 'gps_telemetry'), point('active2', 'drv1'))));
  it("GPS-005: refuses driver1 writing to driver2's Active shift (spoofed driverId)", () =>
    assertFails(addDoc(collection(as('drv1'), 'gps_telemetry'), point('active2', 'drv2'))));
});

// Exactly what MaintenanceService.SubmitMaintenanceRecordAsync writes.
const report = (over = {}) => ({
  taxiId: 'TAXI_001', shiftId: '', managerId: '', maintenanceType: 'BreakdownRepair', issueTitle: 'Brake noise',
  issueDescription: 'grinding', dateLogged: Timestamp.now(), priorityLevel: 'High',
  supportingPhotoUrl: 'https://firebasestorage.googleapis.com/x.jpg', reportedByDriverId: 'drv1', status: 'Reported', ...over,
});

describe('M4 maintenance_logs rules', () => {
  it('MNT-002: driver may file a Reported defect with photo', () =>
    assertSucceeds(addDoc(collection(as('drv1'), 'maintenance_logs'), report())));
  it('MNT-003: driver may file one with an empty supportingPhotoUrl', () =>
    assertSucceeds(addDoc(collection(as('drv1'), 'maintenance_logs'), report({ supportingPhotoUrl: '' }))));
  it('driver may not file one as someone else', () =>
    assertFails(addDoc(collection(as('drv1'), 'maintenance_logs'), report({ reportedByDriverId: 'drv2' }))));
  it('driver may not file one already InProgress', () =>
    assertFails(addDoc(collection(as('drv1'), 'maintenance_logs'), report({ status: 'InProgress' }))));
});
