// Cloud Storage rules tests for LARGA (audit A-07). Local emulators only (demo-larga-rules).
// storage.rules reads roles from Firestore, so the Firestore emulator runs alongside.

const fs = require('fs');
const path = require('path');
const { initializeTestEnvironment, assertSucceeds, assertFails } = require('@firebase/rules-unit-testing');
const { doc, setDoc } = require('firebase/firestore');

const PROJECT_ID = 'demo-larga-rules';
const STORAGE_RULES = fs.readFileSync(path.join(__dirname, '..', '..', 'storage.rules'), 'utf8');
const FIRESTORE_RULES = fs.readFileSync(path.join(__dirname, '..', '..', 'firestore.rules'), 'utf8');

const MANAGER = 'mgr1';
const ASSISTANT = 'asst1';
const DRIVER_A = 'drvA';
const DRIVER_B = 'drvB';
const STRANGER = 'stranger';

const JPEG = { contentType: 'image/jpeg' };
const small = () => new Uint8Array(1024);

let env;
const st = (uid) => (uid ? env.authenticatedContext(uid) : env.unauthenticatedContext()).storage();
const put = (uid, p, bytes = small(), meta = JPEG) => st(uid).ref(p).put(bytes, meta);
const read = (uid, p) => st(uid).ref(p).getMetadata();

before(async () => {
  env = await initializeTestEnvironment({
    projectId: PROJECT_ID,
    storage: { rules: STORAGE_RULES },
    firestore: { rules: FIRESTORE_RULES },
  });
});

beforeEach(async () => {
  await env.clearFirestore();
  await env.clearStorage();
  await env.withSecurityRulesDisabled(async (ctx) => {
    const db = ctx.firestore();
    await setDoc(doc(db, 'users', MANAGER), { role: 'Manager' });
    await setDoc(doc(db, 'users', ASSISTANT), { role: 'Assistant Manager' });
    await setDoc(doc(db, 'users', DRIVER_A), { role: 'Driver' });
    await setDoc(doc(db, 'users', DRIVER_B), { role: 'Driver' });
    const s = ctx.storage();
    await s.ref(`fuel_logs/${DRIVER_B}/shiftB/1/receipt.jpg`).put(small(), JPEG);
    await s.ref(`lto_ids/${DRIVER_B}/licence.jpg`).put(small(), JPEG);
    await s.ref('receipts/ewallet/r1.jpg').put(small(), JPEG);
  });
});

after(async () => { await env.cleanup(); });

describe('Storage: unauthenticated and strangers', () => {
  it('unauthenticated user cannot read a receipt', () => assertFails(read(null, 'receipts/ewallet/r1.jpg')));
  it('unauthenticated user cannot upload', () => assertFails(put(null, `fuel_logs/${DRIVER_A}/s/1/receipt.jpg`)));
  it('signed-in stranger cannot read a licence photo', () => assertFails(read(STRANGER, `lto_ids/${DRIVER_B}/licence.jpg`)));
  it('signed-in stranger cannot upload into a driver folder', () => assertFails(put(STRANGER, `fuel_logs/${STRANGER}/s/1/receipt.jpg`)));
});

describe('Storage: drivers', () => {
  it('driver can upload a receipt photo to their own folder', () => assertSucceeds(put(DRIVER_A, `fuel_logs/${DRIVER_A}/shiftA/1/receipt.jpg`)));
  it('driver can upload a defect photo to their own folder', () => assertSucceeds(put(DRIVER_A, `maintenance_logs/${DRIVER_A}/1/defect.jpg`)));
  it('driver can upload a checklist photo to their own folder', () => assertSucceeds(put(DRIVER_A, `handover_checklists/${DRIVER_A}/shiftA/pre_tires.jpg`)));
  it("driver cannot upload into B's folder", () => assertFails(put(DRIVER_A, `fuel_logs/${DRIVER_B}/shiftB/1/receipt.jpg`)));
  it("driver cannot read B's receipt", () => assertFails(read(DRIVER_A, `fuel_logs/${DRIVER_B}/shiftB/1/receipt.jpg`)));
  it("driver cannot read B's licence photo", () => assertFails(read(DRIVER_A, `lto_ids/${DRIVER_B}/licence.jpg`)));
  it('driver can read their own licence photo', () => assertSucceeds(read(DRIVER_B, `lto_ids/${DRIVER_B}/licence.jpg`)));
  it('driver cannot replace their own licence photo', () => assertFails(put(DRIVER_B, `lto_ids/${DRIVER_B}/licence2.jpg`)));
  it('driver cannot upload a non-image', () =>
    assertFails(put(DRIVER_A, `fuel_logs/${DRIVER_A}/s/1/receipt.jpg`, small(), { contentType: 'application/pdf' })));
  it('driver cannot upload a file of 5 MB or more', () =>
    assertFails(put(DRIVER_A, `fuel_logs/${DRIVER_A}/s/1/receipt.jpg`, new Uint8Array(5 * 1024 * 1024))));
  it('driver cannot read e-wallet receipts', () => assertFails(read(DRIVER_A, 'receipts/ewallet/r1.jpg')));
  it('driver cannot write outside the known folders', () => assertFails(put(DRIVER_A, `profile/${DRIVER_A}.jpg`)));
});

describe('Storage: staff', () => {
  it('manager can read any driver photo', () => assertSucceeds(read(MANAGER, `fuel_logs/${DRIVER_B}/shiftB/1/receipt.jpg`)));
  it('assistant manager can read a licence photo', () => assertSucceeds(read(ASSISTANT, `lto_ids/${DRIVER_B}/licence.jpg`)));
  it('assistant manager can upload an e-wallet receipt', () => assertSucceeds(put(ASSISTANT, 'receipts/ewallet/r2.jpg')));
  it('manager can upload and delete a licence photo', async () => {
    await assertSucceeds(put(MANAGER, `lto_ids/${DRIVER_A}/licence.jpg`));
    await assertSucceeds(st(MANAGER).ref(`lto_ids/${DRIVER_A}/licence.jpg`).delete());
  });
  it('assistant manager cannot delete a licence photo', () => assertFails(st(ASSISTANT).ref(`lto_ids/${DRIVER_B}/licence.jpg`).delete()));
  it('nobody can delete a receipt from a client', () => assertFails(st(MANAGER).ref('receipts/ewallet/r1.jpg').delete()));
});
