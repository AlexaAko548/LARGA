// Web push for the manager portal (chat messages). The browser's FCM token is handed to Blazor
// (WebPushPrompt.razor), which saves it server-side under the signed-in staff member's uid - this
// file never writes Firestore. The uid it's for is kept in IndexedDB so firebase-messaging-sw.js
// can drop any push meant for someone else (a shared office PC that changed hands).
//
// Needs `webPushVapidKey` in firebase-config.js (Firebase Console > Project settings > Cloud
// Messaging > Web Push certificates). Without it web push stays off.
import * as config from './firebase-config.js';

const SDK = 'https://www.gstatic.com/firebasejs/10.12.2';
const SW_URL = '/firebase-messaging-sw.js';
const DEVICE_KEY = 'larga.webpush.deviceId';
const TOKEN_KEY = 'larga.webpush.token';
const DISMISSED_KEY = 'larga.webpush.dismissed';

let messagingPromise = null;

function supported() {
    return 'serviceWorker' in navigator && 'PushManager' in window && 'Notification' in window && 'indexedDB' in window;
}

async function getMessaging() {
    if (!messagingPromise) {
        messagingPromise = (async () => {
            const appModule = await import(`${SDK}/firebase-app.js`);
            const messagingModule = await import(`${SDK}/firebase-messaging.js`);
            // firebase-auth.js may have created the default app already (same SDK URL, same module).
            const app = appModule.getApps().length ? appModule.getApp() : appModule.initializeApp(config.firebaseConfig);
            return { module: messagingModule, messaging: messagingModule.getMessaging(app) };
        })();
    }
    return messagingPromise;
}

// --- the uid the service worker checks pushes against (IndexedDB: larga-push / kv / uid) ---
function openDb() {
    return new Promise((resolve, reject) => {
        const request = indexedDB.open('larga-push', 1);
        request.onupgradeneeded = () => request.result.createObjectStore('kv');
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(request.error);
    });
}

async function setSignedInUid(uid) {
    const db = await openDb();
    await new Promise((resolve, reject) => {
        const tx = db.transaction('kv', 'readwrite');
        if (uid) tx.objectStore('kv').put(uid, 'uid'); else tx.objectStore('kv').delete('uid');
        tx.oncomplete = resolve;
        tx.onerror = () => reject(tx.error);
    });
    db.close();
}

function safeGet(key) {
    try { return localStorage.getItem(key); } catch { return null; }
}

function safeSet(key, value) {
    try { if (value === null) localStorage.removeItem(key); else localStorage.setItem(key, value); } catch { /* private mode */ }
}

function deviceId() {
    let id = safeGet(DEVICE_KEY);
    if (!id) {
        id = (crypto.randomUUID ? crypto.randomUUID() : String(Date.now()) + Math.random()).replace(/-/g, '');
        safeSet(DEVICE_KEY, id);
    }
    return id;
}

/** 'unsupported' | 'unconfigured' | 'default' | 'granted' | 'denied', plus whether the prompt was dismissed. */
export function largaWebPushStatus() {
    if (!supported()) return { state: 'unsupported', dismissed: true };
    if (!config.webPushVapidKey) return { state: 'unconfigured', dismissed: true };
    return { state: Notification.permission, dismissed: safeGet(DISMISSED_KEY) === '1' };
}

export function largaWebPushDismiss() {
    safeSet(DISMISSED_KEY, '1');
}

/**
 * Turns on push for `uid` (the signed-in staff member - Blazor passes it from the server session).
 * `ask`: show the browser's permission prompt (only from a click). Returns { ok, token, deviceId }.
 */
export async function largaWebPushEnable(uid, ask) {
    try {
        if (!supported() || !config.webPushVapidKey || !uid) return { ok: false };

        let permission = Notification.permission;
        if (permission === 'default' && ask) {
            permission = await Notification.requestPermission();
        }
        if (permission !== 'granted') {
            if (permission === 'denied') largaWebPushDismiss();
            return { ok: false };
        }

        // Recorded before the token exists, so the worker never shows a push for a previous user.
        await setSignedInUid(uid);
        const registration = await navigator.serviceWorker.register(SW_URL, { scope: '/' });
        const { module, messaging } = await getMessaging();
        const token = await module.getToken(messaging, { vapidKey: config.webPushVapidKey, serviceWorkerRegistration: registration });
        if (!token) return { ok: false };

        safeSet(TOKEN_KEY, token);
        return { ok: true, token, deviceId: deviceId() };
    } catch (error) {
        console.warn('Web push could not be turned on:', error);
        return { ok: false };
    }
}

/**
 * Sign-out: this browser stops showing anyone's pushes and its FCM token is deleted. Returns the
 * token it had (or null), so Blazor can remove it from the account server-side.
 */
export async function largaWebPushDisable() {
    const token = safeGet(TOKEN_KEY);
    try {
        await setSignedInUid(null);
    } catch { /* IndexedDB blocked - the token is deleted below anyway */ }

    if (token && supported() && config.webPushVapidKey) {
        try {
            const { module, messaging } = await getMessaging();
            await module.deleteToken(messaging);
        } catch (error) {
            console.warn('Web push token could not be deleted:', error);
        }
    }
    safeSet(TOKEN_KEY, null);
    return token;
}
