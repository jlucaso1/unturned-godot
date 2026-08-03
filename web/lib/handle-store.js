// Remembers the folder the player picked, so they pick it once and not once per page load.
//
// FileSystemDirectoryHandle is structured-cloneable, which means IndexedDB can store it — that is the
// whole trick. What IndexedDB cannot store is the *permission*: on a new page load the handle comes back
// but its state is usually "prompt", and re-granting must happen inside a user gesture. So a restored
// session is one click ("Reconnect"), never zero-click-and-silently-reading-your-disk, which is the
// browser being careful on the player's behalf rather than an API gap.

const DATABASE = "unturned-web";
const STORE = "handles";
const KEY = "install-root";

function openDatabase() {
    return new Promise((resolve, reject) => {
        const request = indexedDB.open(DATABASE, 1);
        request.onupgradeneeded = () => {
            if (!request.result.objectStoreNames.contains(STORE)) request.result.createObjectStore(STORE);
        };
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(request.error);
    });
}

function transact(database, mode, run) {
    return new Promise((resolve, reject) => {
        const transaction = database.transaction(STORE, mode);
        const request = run(transaction.objectStore(STORE));
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(request.error);
        transaction.onabort = () => reject(transaction.error);
    });
}

export async function saveHandle(handle) {
    const database = await openDatabase();
    try {
        await transact(database, "readwrite", (store) => store.put(handle, KEY));
    } finally {
        database.close();
    }
}

// The stored handle, or null when there is none. A handle whose folder was moved or deleted still
// deserializes fine and only fails when read, so callers probe it before trusting it.
export async function loadHandle() {
    const database = await openDatabase();
    try {
        return (await transact(database, "readonly", (store) => store.get(KEY))) ?? null;
    } catch {
        // A handle written by an older browser profile can fail to deserialize; that is a cache miss,
        // not an error worth showing anyone.
        return null;
    } finally {
        database.close();
    }
}

export async function forgetHandle() {
    const database = await openDatabase();
    try {
        await transact(database, "readwrite", (store) => store.delete(KEY));
    } finally {
        database.close();
    }
}
