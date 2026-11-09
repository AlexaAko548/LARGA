import { firebaseConfig } from './firebase-config.js';

let firebaseApp = null;
let firebaseAuth = null;

async function initializeFirebase() {
    if (firebaseApp && firebaseAuth) {
        return firebaseAuth;
    }

    if (!window.firebaseSdk) {
        const firebaseAppModule = await import('https://www.gstatic.com/firebasejs/10.12.2/firebase-app.js');
        const firebaseAuthModule = await import('https://www.gstatic.com/firebasejs/10.12.2/firebase-auth.js');
        const firebaseFirestoreModule = await import('https://www.gstatic.com/firebasejs/10.12.2/firebase-firestore.js');

        window.firebaseSdk = {
            app: firebaseAppModule.initializeApp,
            auth: firebaseAuthModule.getAuth,
            firestore: firebaseFirestoreModule.getFirestore,
            getDocument: firebaseFirestoreModule.getDoc,
            document: firebaseFirestoreModule.doc,
            setDocument: firebaseFirestoreModule.setDoc,
            getDocuments: firebaseFirestoreModule.getDocs,
            collection: firebaseFirestoreModule.collection,
            query: firebaseFirestoreModule.query,
            where: firebaseFirestoreModule.where,
            signIn: firebaseAuthModule.signInWithEmailAndPassword,
            signOut: firebaseAuthModule.signOut,
            createUser: firebaseAuthModule.createUserWithEmailAndPassword,
            updateProfile: firebaseAuthModule.updateProfile,
            updatePassword: firebaseAuthModule.updatePassword,
            sendPasswordResetEmail: firebaseAuthModule.sendPasswordResetEmail,
            setPersistence: firebaseAuthModule.setPersistence,
            browserLocalPersistence: firebaseAuthModule.browserLocalPersistence
        };
    }

    firebaseApp = window.firebaseSdk.app(firebaseConfig);
    firebaseAuth = window.firebaseSdk.auth(firebaseApp);

    await window.firebaseSdk.setPersistence(firebaseAuth, window.firebaseSdk.browserLocalPersistence);

    return firebaseAuth;
}

export async function largaFirebaseSignIn(email, password) {
    try {
        const auth = await initializeFirebase();
        const cred = await window.firebaseSdk.signIn(auth, email, password);
        const roleResult = await getManagerRole(cred.user);

        if (!roleResult.ok) {
            await window.firebaseSdk.signOut(auth);
            return {
                ok: false,
                code: 'manager-only',
                message: roleResult.message
            };
        }

        return {
            ok: true,
            uid: cred.user?.uid || null,
            email: cred.user?.email || null,
            role: 'manager',
            // Verified server-side (ManagerSignInService) before the session cookie is issued.
            idToken: await cred.user.getIdToken()
        };
    } catch (error) {
        return {
            ok: false,
            message: error?.message || 'Login failed.'
        };
    }
}

async function getManagerRole(user) {
    const firestore = window.firebaseSdk.firestore(firebaseApp);
    let userSnapshot = await window.firebaseSdk.getDocument(
        window.firebaseSdk.document(firestore, 'users', user.uid)
    );

    if (!userSnapshot.exists()) {
        const userQuery = window.firebaseSdk.query(
            window.firebaseSdk.collection(firestore, 'users'),
            window.firebaseSdk.where('email', '==', user.email)
        );
        const matchingUsers = await window.firebaseSdk.getDocuments(userQuery);
        userSnapshot = matchingUsers.docs[0];

        if (!userSnapshot) {
            return {
                ok: false,
                message: 'No user profile was found for this Firebase account.'
            };
        }
    }

    // Same portal roles as ManagerSignInService.StaffRoles.
    const role = userSnapshot.data()?.role;
    if (typeof role !== 'string' || !['manager', 'assistant manager'].includes(role.trim().toLowerCase())) {
        return {
            ok: false,
            message: 'This account is not authorized to access the manager portal.'
        };
    }

    return { ok: true };
}

export async function largaFirebaseRegister(email, password, displayName, fleetSize, phone) {
    try {
        const auth = await initializeFirebase();
        const cred = await window.firebaseSdk.createUser(auth, email, password);

        if (displayName && cred.user && typeof window.firebaseSdk.updateProfile === 'function') {
            await window.firebaseSdk.updateProfile(cred.user, { displayName });
        }

        const firestore = window.firebaseSdk.firestore(firebaseApp);
        await window.firebaseSdk.setDocument(
            window.firebaseSdk.document(firestore, 'users', cred.user.uid),
            {
                email: cred.user.email || email,
                fullName: displayName || '',
                role: 'Manager',
                fleetSize: fleetSize || '',
                phoneNumber: phone || ''
            },
            { merge: true }
        );

        await window.firebaseSdk.signOut(auth);

        return {
            ok: true,
            uid: cred.user?.uid || null,
            email: cred.user?.email || null,
            displayName: displayName || null
        };
    } catch (error) {
        return {
            ok: false,
            message: error?.message || 'Registration failed.'
        };
    }
}

export async function largaFirebaseSignOut() {
    const auth = await initializeFirebase();
    await window.firebaseSdk.signOut(auth);
    return true;
}

// Profile > Settings > Update Password. Signing in with the current password proves it's
// right and gives Firebase the recent sign-in it requires before a password change - the
// same approach as the mobile app's ChangePasswordViewModel.
export async function largaFirebaseChangePassword(email, currentPassword, newPassword) {
    try {
        const auth = await initializeFirebase();
        const cred = await window.firebaseSdk.signIn(auth, email, currentPassword);
        await window.firebaseSdk.updatePassword(cred.user, newPassword);
        await window.firebaseSdk.signOut(auth);
        return { ok: true };
    } catch (error) {
        const code = error?.code || '';
        if (code === 'auth/wrong-password' || code === 'auth/invalid-credential' || code === 'auth/invalid-login-credentials') {
            return { ok: false, message: 'Current password is incorrect.' };
        }
        if (code === 'auth/too-many-requests') {
            return { ok: false, message: 'Too many attempts. Please wait a few minutes and try again.' };
        }
        if (code === 'auth/weak-password') {
            return { ok: false, message: 'Choose a stronger new password.' };
        }
        return { ok: false, message: 'Could not update your password. Please try again.' };
    }
}

// Forgot password page. Always reports success to the caller unless the request itself
// failed, so the page never reveals whether an email has an account.
export async function largaFirebaseSendPasswordReset(email) {
    try {
        const auth = await initializeFirebase();
        await window.firebaseSdk.sendPasswordResetEmail(auth, email);
        return { ok: true };
    } catch (error) {
        const code = error?.code || '';
        if (code === 'auth/user-not-found' || code === 'auth/invalid-email') {
            return { ok: true };
        }
        if (code === 'auth/too-many-requests') {
            return { ok: false, message: 'Too many attempts. Please wait a few minutes and try again.' };
        }
        return { ok: false, message: 'Could not send the reset email. Please try again.' };
    }
}

window.largaFirebaseSignIn = largaFirebaseSignIn;
window.largaFirebaseRegister = largaFirebaseRegister;
window.largaFirebaseSignOut = largaFirebaseSignOut;
window.largaFirebaseChangePassword = largaFirebaseChangePassword;
window.largaFirebaseSendPasswordReset = largaFirebaseSendPasswordReset;