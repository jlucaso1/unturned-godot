// The fallback for browsers without showDirectoryPicker: `<input type="file" webkitdirectory>`.
//
// Despite the vendor-prefixed name this is supported in Firefox and Safari as well as Chromium, and it is
// the only way those browsers can read a folder the player selects. It is strictly worse than
// HandleFs and the differences are worth stating plainly, because they drive the recommendation in
// docs/WEB-EXPORT.md:
//
//   * The whole tree is enumerated up front. The picker returns one File per file in the folder — an
//     Unturned install is ~100k of them — so selection takes seconds and costs memory for the listing.
//     No file *content* is read, so it is metadata-only, but it is not free.
//   * Nothing persists. There is no handle to store, so the player re-picks the folder on every load,
//     where HandleFs can restore a saved handle with one click (or none, if the grant survives).
//   * Read-only, always. No counterpart to a readwrite handle, so an asset cache cannot be written back
//     next to the install and has to live in the browser's own storage.
//
// The interface is deliberately identical to HandleFs so catalog.js never learns which one it got.

import { baseName, dirName, normalize } from "./paths.js";
import { isCaseInsensitiveFilesystem } from "./platform.js";
import { ReadOnlyFs } from "./read-only-fs.js";

export function supportsDirectoryInput() {
    if (typeof document === "undefined") return false;
    return "webkitdirectory" in document.createElement("input");
}

export class ListingFs extends ReadOnlyFs {
    #files = new Map(); // normalized path -> File
    #dirs = new Set([""]);
    // Directory path -> its immediate children, built once. Without it every listDir scanned the whole
    // flat file map, and the probe calls listDir per workshop item and per map's Heightmaps folder — so
    // a player selecting a Steam library (the only way to reach workshop maps in this backend) paid
    // total-files × directories-listed, on a listing that can hold every file of every installed game.
    #children = new Map();
    // Lowercased path -> the spelling in #files, built only on a case-insensitive host. HandleFs asks
    // the browser, which asks the OS, so it folds case exactly where the platform does; this backend
    // resolves out of its own index and would otherwise be the only case-sensitive filesystem on a
    // Windows machine — dropping a workshop map whose author shipped `level.dat` even though the
    // desktop's File.Exists finds it there.
    #folded = null;
    #name;

    // `fileList` is the FileList from a webkitdirectory input. Every entry's webkitRelativePath starts
    // with the picked folder's own name, which is stripped so paths are relative to the root exactly as
    // in HandleFs.
    constructor(fileList, { name = null, caseInsensitive = isCaseInsensitiveFilesystem() } = {}) {
        super();
        let rootName = name;
        if (caseInsensitive) this.#folded = new Map();
        for (const file of fileList) {
            const relative = file.webkitRelativePath || file.name;
            // Test the normalized string, not the split array: "".split("/") is [""], so a length check
            // lets an entry with no usable path through and files it under the root's own key.
            const cleaned = normalize(relative);
            if (cleaned === "") continue;
            const parts = cleaned.split("/");
            if (rootName === null && parts.length > 1) rootName = parts[0];
            const path = parts.length > 1 ? parts.slice(1).join("/") : parts[0];
            this.#files.set(path, file);
            this.#addChild(dirName(path), { name: baseName(path), kind: "file", path });
            for (let dir = dirName(path); dir !== ""; dir = dirName(dir)) {
                if (this.#dirs.has(dir)) break; // this dir and every parent are already registered
                this.#dirs.add(dir);
                this.#addChild(dirName(dir), { name: baseName(dir), kind: "directory", path: dir });
            }
        }
        for (const entries of this.#children.values()) {
            entries.sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0));
        }
        if (this.#folded !== null) {
            // Built after the loop so an exact spelling always wins over a folded one, and the first
            // spelling wins among folded duplicates rather than the last.
            for (const path of this.#files.keys()) {
                const key = path.toLowerCase();
                if (!this.#folded.has(key)) this.#folded.set(key, path);
            }
            for (const dir of this.#dirs) {
                const key = `d:${dir.toLowerCase()}`;
                if (!this.#folded.has(key)) this.#folded.set(key, dir);
            }
        }
        this.#name = rootName ?? "";
    }

    #addChild(directory, entry) {
        const siblings = this.#children.get(directory);
        if (siblings === undefined) this.#children.set(directory, [entry]);
        else siblings.push(entry);
    }

    // The stored spelling of a path, or null. Exact match first; case folding only where the host folds.
    #resolveFile(path) {
        const key = normalize(path);
        if (this.#files.has(key)) return key;
        return this.#folded?.get(key.toLowerCase()) ?? null;
    }

    #resolveDir(path) {
        const key = normalize(path);
        if (this.#dirs.has(key)) return key;
        return this.#folded?.get(`d:${key.toLowerCase()}`) ?? null;
    }

    get name() {
        return this.#name;
    }

    get fileCount() {
        return this.#files.size;
    }

    async isDirectory(path) {
        return this.#resolveDir(path) !== null;
    }

    async isFile(path) {
        return this.#resolveFile(path) !== null;
    }

    async exists(path) {
        return this.#resolveFile(path) !== null || this.#resolveDir(path) !== null;
    }

    async listDir(path) {
        const prefix = this.#resolveDir(path);
        if (prefix === null) return [];
        // Already sorted by name in the constructor, and copied so a caller cannot mutate the index.
        return [...(this.#children.get(prefix) ?? [])];
    }

    async stat(path) {
        const key = this.#resolveFile(path);
        if (key === null) return null;
        const file = this.#files.get(key);
        return { path: key, size: file.size, lastModified: file.lastModified };
    }

    async file(path) {
        const key = this.#resolveFile(path);
        return key === null ? null : this.#files.get(key);
    }

    // Same contract as HandleFs.walk: `filter` selects files, `prune` skips directories. There is no
    // tree to stop descending here, so pruning is answered by testing each of a file's ancestors — the
    // result has to match what a real traversal would have produced, not merely be cheap.
    async walk(path = "", { filter = null, prune = null, maxEntries = Infinity } = {}) {
        const root = this.#resolveDir(path) ?? normalize(path);
        const head = root === "" ? "" : `${root}/`;
        const found = [];
        for (const key of this.#files.keys()) {
            if (!key.startsWith(head)) continue;
            if (prune !== null && this.#isPruned(key, root, prune)) continue;
            if (filter !== null && !filter({ name: baseName(key), kind: "file", path: key })) continue;
            found.push(key);
            if (found.length >= maxEntries) break;
        }
        return found;
    }

    #isPruned(filePath, root, prune) {
        for (let dir = dirName(filePath); dir !== root && dir !== ""; dir = dirName(dir)) {
            if (prune({ name: baseName(dir), kind: "directory", path: dir })) return true;
        }
        return false;
    }

    // The listing is a snapshot taken when the player picked the folder; there is nothing to invalidate
    // short of picking again.
    invalidate() {}
}
