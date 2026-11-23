// Copy this file to firebase-config.js (same folder) and replace the placeholder values
// with your Firebase web app config (Firebase Console > Project settings > Your apps > Web).
// firebase-config.js is ignored by .gitignore - do not commit it.
export const firebaseConfig = {
    apiKey: "YOUR_API_KEY",
    authDomain: "YOUR_PROJECT.firebaseapp.com",
    projectId: "YOUR_PROJECT_ID",
    storageBucket: "YOUR_PROJECT.firebasestorage.app",
    messagingSenderId: "YOUR_MESSAGING_SENDER_ID",
    appId: "YOUR_APP_ID"
};

// Web push (chat notifications in the browser): the public key of the key pair under
// Firebase Console > Project settings > Cloud Messaging > Web Push certificates. Leave it empty
// to keep web push off.
export const webPushVapidKey = "YOUR_WEB_PUSH_VAPID_PUBLIC_KEY";
