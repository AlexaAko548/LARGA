// M2 alpha re-run (BRP-026 and the manager-only ledger writes): firestore.rules (repo copy) in the local emulator only.
// Run: NODE_PATH=tests/rules/node_modules tests/rules/node_modules/.bin/mocha --timeout 20000 LARGA.Tests/Rules/brp.rules.test.js
const fs = require('fs');
const path = require('path');
const { initializeTestEnvironment, assertSucceeds, assertFails } = require('@firebase/rules-unit-testing');
const { doc, setDoc, addDoc, getDoc, getDocs, collection, query, where, Timestamp, serverTimestamp } = require('firebase/firestore');

const PROJECT_ID = 'demo-larga-tests';
const RULES = fs.readFileSync(path.join(__dirname, '..', '..', 'firestore.rules'), 'utf8');
let env;
const as = (uid) => env.authenticatedContext(uid).firestore();

before(async () => {
  env = await initializeTestEnvironment({ projectId: PROJECT_ID, firestore: { rules: RULES, host: '127.0.0.1', port: 8080 } });
});
beforeEach(async () => {
  await env.clearFirestore();
  await env.withSecurityRulesDisabled(async (ctx) => {
    const db = ctx.firestore();
    await setDoc(doc(db, 'users', 'drv1'), { role: 'Driver', fullName: 'Driver One' });
    await setDoc(doc(db, 'users', 'drv2'), { role: 'Driver', fullName: 'Driver Two' });
    await setDoc(doc(db, 'users', 'mgr'), { role: 'Manager', fullName: 'Manager' });
    await setDoc(doc(db, 'shifts', 's1'), { driverId: 'drv1', taxiId: 'TAXI_001', status: 'Completed' });
    await setDoc(doc(db, 'shifts', 's2'), { driverId: 'drv2', taxiId: 'TAXI_002', status: 'Completed' });
    await setDoc(doc(db, 'boundary_payments', 'p1'), { shiftId: 's1', driverId: 'drv1', amountPaid: 800 });
    await setDoc(doc(db, 'boundary_payments', 'p2'), { shiftId: 's2', driverId: 'drv2', amountPaid: 400 });
    await setDoc(doc(db, 'debt_adjustments', 'a2'), { driverId: 'drv2', amount: 200 });
    await setDoc(doc(db, 'ledger_locks', 'drv2'), { version: 3 });
  });
});
after(async () => { await env.cleanup(); });

const pay = (driverId) => ({ shiftId: 's2', driverId, amountPaid: 400, paymentStatus: 'Partial', timestamp: Timestamp.now() });

describe('BRP-026 driver cannot read another driver\'s ledger', () => {
  it("driver1 cannot read driver2's payment by ID", () => assertFails(getDoc(doc(as('drv1'), 'boundary_payments', 'p2'))));
  it("driver1 cannot query driver2's payments", () =>
    assertFails(getDocs(query(collection(as('drv1'), 'boundary_payments'), where('driverId', '==', 'drv2')))));
  it('driver1 cannot list all payments', () => assertFails(getDocs(collection(as('drv1'), 'boundary_payments'))));
  it("driver1 cannot read driver2's shift", () => assertFails(getDoc(doc(as('drv1'), 'shifts', 's2'))));
  it("driver1 cannot query driver2's shifts", () =>
    assertFails(getDocs(query(collection(as('drv1'), 'shifts'), where('driverId', '==', 'drv2')))));
  it("driver1 cannot query driver2's adjustments", () =>
    assertFails(getDocs(query(collection(as('drv1'), 'debt_adjustments'), where('driverId', '==', 'drv2')))));
  it("driver1 cannot read driver2's ledger version", () => assertFails(getDoc(doc(as('drv1'), 'ledger_locks', 'drv2'))));
  it('baseline: driver1 can query their own payments (what the driver Ledger does)', () =>
    assertSucceeds(getDocs(query(collection(as('drv1'), 'boundary_payments'), where('driverId', '==', 'drv1')))));
});

describe('ledger writes are staff-only', () => {
  it('driver cannot record a payment for themselves', () =>
    assertFails(setDoc(doc(as('drv2'), 'boundary_payments', 's2_PAY_20261026000000000'), pay('drv2'))));
  it('driver cannot write a debt credit', () =>
    assertFails(setDoc(doc(as('drv2'), 'debt_adjustments', 'x'), { driverId: 'drv2', amount: -500 })));
  it('manager can record a payment', () =>
    assertSucceeds(setDoc(doc(as('mgr'), 'boundary_payments', 's2_PAY_20261026000000000'), pay('drv2'))));
  it("manager app's Quick Ledger audit entry is allowed (QuickLedgerService.WriteAuditAsync)", () =>
    assertSucceeds(addDoc(collection(as('mgr'), 'audit_logs'), {
      userId: 'mgr', actionType: 'QuickLedgerDebtPaymentRecorded', auditLogDetails: 'x', timestamp: serverTimestamp(),
    })));
  it('manager cannot record a 0 or negative payment', async () => {
    await assertFails(setDoc(doc(as('mgr'), 'boundary_payments', 'z'), { ...pay('drv2'), amountPaid: 0 }));
    await assertFails(setDoc(doc(as('mgr'), 'boundary_payments', 'n'), { ...pay('drv2'), amountPaid: -100 }));
  });
});
