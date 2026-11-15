// Service worker for the manager portal's chat pushes (ManagerWeb ChatPushService). Chat pushes
// are data-only, so nothing shows until this worker has checked that the push's recipientUid is
// the staff member signed in on this browser (saved by web-push.js in IndexedDB at sign-in,
// cleared at sign-out). A push for anyone else, or with no one signed in, is dropped.
// No Firebase SDK is needed here: the raw push event carries FCM's { data: {...} } payload.

const CHAT_TYPE = 'chat_message';

function signedInUid() {
    return new Promise((resolve) => {
        const request = indexedDB.open('larga-push', 1);
        request.onupgradeneeded = () => request.result.createObjectStore('kv');
        request.onerror = () => resolve(null);
        request.onsuccess = () => {
            const db = request.result;
            try {
                const get = db.transaction('kv', 'readonly').objectStore('kv').get('uid');
                get.onsuccess = () => { resolve(typeof get.result === 'string' ? get.result : null); db.close(); };
                get.onerror = () => { resolve(null); db.close(); };
            } catch {
                resolve(null);
                db.close();
            }
        };
    });
}

async function handlePush(event) {
    let payload = null;
    try { payload = event.data ? event.data.json() : null; } catch { return; }
    const data = (payload && payload.data) || payload || {};
    if (data.type !== CHAT_TYPE || !data.recipientUid || !data.conversationId) return;

    const uid = await signedInUid();
    if (!uid || uid !== data.recipientUid) return;

    // The portal is open and in front: its Messages button already shows the new message.
    const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
    if (windows.some((w) => w.focused)) return;

    await self.registration.showNotification(data.title || 'LARGA', {
        body: data.body || '',
        tag: `chat_${data.conversationId}`, // a newer message replaces the older one
        renotify: true,
        icon: '/images/larga-logo-taxi.png',
        data: { conversationId: data.conversationId, recipientUid: data.recipientUid },
    });
}

self.addEventListener('push', (event) => event.waitUntil(handlePush(event)));

self.addEventListener('notificationclick', (event) => {
    event.notification.close();
    const { conversationId, recipientUid } = event.notification.data || {};
    if (!conversationId) return;

    // The dashboard checks the login and that `for` is the signed-in user before opening the chat.
    const url = `/dashboard?openChat=${encodeURIComponent(conversationId)}&for=${encodeURIComponent(recipientUid || '')}`;
    event.waitUntil((async () => {
        const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
        const existing = windows.find((w) => new URL(w.url).origin === self.location.origin);
        if (existing) {
            try {
                await existing.focus();
                return await existing.navigate(url);
            } catch {
                // Not controlled by this worker yet - open a new tab instead.
            }
        }
        return self.clients.openWindow(url);
    })());
});

self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', (event) => event.waitUntil(self.clients.claim()));
