// A read-only, path-addressed view over a directory the player picked, backed by the File System Access
// API's FileSystemDirectoryHandle.
//
// This is the piece that lets a web build read a real Unturned install without anybody shipping,
// uploading or zipping the game's content: the browser hands the page a capability scoped to exactly the
// one folder the player chose, the bytes stay on their disk, and nothing is copied anywhere. Same
// content rule as the desktop build (see NOTICE.md) — the port ships no game data and reads the player's
// own install.
//
// Everything here is async because the underlying API is. The parsers in core/ are synchronous, so the
// bridge described in docs/WEB-EXPORT.md stages the files a load needs through this class first and only
// then hands them to the (unchanged) synchronous readers.

import { baseName, dirName, normalize, segments } from "./paths.js";

// Whether this browser can show a directory picker at all. Chromium-based browsers can; Firefox and
// Safari cannot, and fall back to ListingFs (see listing-fs.js).
export function supportsDirectoryPicker() {
    return typeof globalThis.showDirectoryPicker === "function";
}

// Opens the picker and returns the chosen directory handle, or null when the player cancels.
export async function pickDirectory(options = {}) {
    try {
        return await globalThis.showDirectoryPicker({
            id: options.id ?? "unturned-install",
            mode: options.mode ?? "read",
            // Games are the obvious neighbourhood to start in, and the browser remembers the last pick
            // per `id` anyway, so this only matters on the very first run.
            startIn: options.startIn ?? "desktop",
        });
    } catch (error) {
        if (error?.name === "AbortError") return null;
        throw error;
    }
}

// Asks whether we still hold read permission on a handle, and requests it if not. Chrome drops the grant
// when the tab closes unless the handle was persisted and the player re-confirms, so a restored handle
// always goes through this — and requesting must happen inside a user gesture.
export async function ensureReadPermission(handle, { prompt = false } = {}) {
    const descriptor = { mode: "read" };
    if ((await handle.queryPermission?.(descriptor)) === "granted") return true;
    if (!prompt) return false;
    return (await handle.requestPermission?.(descriptor)) === "granted";
}

export class HandleFs {
    #root;
    // path -> Promise<FileSystemDirectoryHandle | null>. Resolving "Maps/PEI/Landscape/Heightmaps" costs
    // four round-trips through the API, and a map scan walks the same prefixes over and over, so the
    // in-flight promise is cached rather than the result: two concurrent lookups share one walk.
    #dirs = new Map();
    #files = new Map();

    constructor(rootHandle) {
        this.#root = rootHandle;
        this.#dirs.set("", Promise.resolve(rootHandle));
    }

    // What the picker called the folder. Used only to tell the player what they selected.
    get name() {
        return this.#root.name;
    }

    get rootHandle() {
        return this.#root;
    }

    async directoryHandle(path) {
        const key = normalize(path);
        let pending = this.#dirs.get(key);
        if (pending === undefined) {
            pending = this.#resolveDirectory(key);
            this.#dirs.set(key, pending);
        }
        return pending;
    }

    async #resolveDirectory(key) {
        const parent = await this.directoryHandle(dirName(key));
        if (parent === null) return null;
        try {
            return await parent.getDirectoryHandle(baseName(key));
        } catch (error) {
            // NotFoundError: no such entry. TypeMismatchError: it exists but is a file.
            if (error?.name === "NotFoundError" || error?.name === "TypeMismatchError") return null;
            throw error;
        }
    }

    async fileHandle(path) {
        const key = normalize(path);
        let pending = this.#files.get(key);
        if (pending === undefined) {
            pending = this.#resolveFile(key);
            this.#files.set(key, pending);
        }
        return pending;
    }

    async #resolveFile(key) {
        if (key === "") return null;
        const parent = await this.directoryHandle(dirName(key));
        if (parent === null) return null;
        try {
            return await parent.getFileHandle(baseName(key));
        } catch (error) {
            if (error?.name === "NotFoundError" || error?.name === "TypeMismatchError") return null;
            throw error;
        }
    }

    async isDirectory(path) {
        return (await this.directoryHandle(path)) !== null;
    }

    async isFile(path) {
        return (await this.fileHandle(path)) !== null;
    }

    async exists(path) {
        return (await this.isFile(path)) || (await this.isDirectory(path));
    }

    // Entry names directly inside a directory, as { name, kind, path }. Missing directories list empty
    // rather than throwing: MapCatalog treats an absent Bundles/Workshop/Maps as "no workshop maps", not
    // as an error, and the probe mirrors that.
    async listDir(path) {
        const handle = await this.directoryHandle(path);
        if (handle === null) return [];
        const prefix = normalize(path);
        const entries = [];
        for await (const [name, entry] of handle.entries()) {
            entries.push({ name, kind: entry.kind, path: prefix === "" ? name : `${prefix}/${name}` });
        }
        entries.sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0));
        return entries;
    }

    async listDirectories(path) {
        return (await this.listDir(path)).filter((entry) => entry.kind === "directory");
    }

    async listFiles(path) {
        return (await this.listDir(path)).filter((entry) => entry.kind === "file");
    }

    // Size and last-modified time without reading a byte — File objects are lazy views over the disk
    // file, so this stays cheap even for the ~1.4 GB masterbundle.
    async stat(path) {
        const handle = await this.fileHandle(path);
        if (handle === null) return null;
        const file = await handle.getFile();
        return { path: normalize(path), size: file.size, lastModified: file.lastModified };
    }

    async file(path) {
        const handle = await this.fileHandle(path);
        return handle === null ? null : handle.getFile();
    }

    async readFile(path) {
        const file = await this.file(path);
        return file === null ? null : new Uint8Array(await file.arrayBuffer());
    }

    // A byte range, which is how anything large has to be read here: a File is a Blob, and slicing one
    // does not materialize the whole file. The masterbundle is read this way (see docs/WEB-EXPORT.md) —
    // the desktop build's File.ReadAllBytes over 1.4 GB has no counterpart in a 32-bit wasm heap.
    async readRange(path, offset, length) {
        const file = await this.file(path);
        if (file === null) return null;
        const start = Math.max(0, Math.min(offset, file.size));
        const end = Math.max(start, Math.min(start + length, file.size));
        return new Uint8Array(await file.slice(start, end).arrayBuffer());
    }

    async readText(path) {
        const file = await this.file(path);
        return file === null ? null : file.text();
    }

    // A blob: URL for an <img>. The caller owns it and must URL.revokeObjectURL it.
    async objectUrl(path) {
        const file = await this.file(path);
        return file === null ? null : URL.createObjectURL(file);
    }

    // Every file under a subtree, as { path, size }. This is what builds the index the engine bridge
    // needs up front; `filter` keeps a scan of a whole Steam library from walking into Bundles/ (tens of
    // thousands of entries) when only Maps/ is wanted.
    async walk(path = "", { filter = null, maxEntries = Infinity } = {}) {
        const found = [];
        const queue = [normalize(path)];
        while (queue.length > 0 && found.length < maxEntries) {
            const current = queue.shift();
            for (const entry of await this.listDir(current)) {
                if (filter !== null && !filter(entry)) continue;
                if (entry.kind === "directory") {
                    queue.push(entry.path);
                } else {
                    found.push(entry.path);
                    if (found.length >= maxEntries) break;
                }
            }
        }
        return found;
    }

    // Drops the resolved-handle caches. The picked directory is a live view of a folder the player can
    // edit underneath us; anything that wants to see a map they just subscribed to calls this first.
    invalidate() {
        this.#dirs = new Map([["", Promise.resolve(this.#root)]]);
        this.#files = new Map();
    }
}

// Resolves a path to the handle of a *directory*, used when the probe needs to hand a subtree to
// something that expects its own root (a map folder, a Steam library).
export async function subtree(fs, path) {
    const handle = await fs.directoryHandle(path);
    return handle === null ? null : new HandleFs(handle);
}

export { segments };
