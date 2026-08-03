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

export function supportsDirectoryInput() {
    if (typeof document === "undefined") return false;
    return "webkitdirectory" in document.createElement("input");
}

export class ListingFs {
    #files = new Map(); // normalized path -> File
    #dirs = new Set([""]);
    #name;

    // `fileList` is the FileList from a webkitdirectory input. Every entry's webkitRelativePath starts
    // with the picked folder's own name, which is stripped so paths are relative to the root exactly as
    // in HandleFs.
    constructor(fileList, { name = null } = {}) {
        let rootName = name;
        for (const file of fileList) {
            const relative = file.webkitRelativePath || file.name;
            const parts = normalize(relative).split("/");
            if (parts.length === 0) continue;
            if (rootName === null && parts.length > 1) rootName = parts[0];
            const path = parts.length > 1 ? parts.slice(1).join("/") : parts[0];
            this.#files.set(path, file);
            for (let dir = dirName(path); dir !== ""; dir = dirName(dir)) this.#dirs.add(dir);
        }
        this.#name = rootName ?? "";
    }

    get name() {
        return this.#name;
    }

    get fileCount() {
        return this.#files.size;
    }

    async isDirectory(path) {
        return this.#dirs.has(normalize(path));
    }

    async isFile(path) {
        return this.#files.has(normalize(path));
    }

    async exists(path) {
        const key = normalize(path);
        return this.#files.has(key) || this.#dirs.has(key);
    }

    async listDir(path) {
        const prefix = normalize(path);
        const head = prefix === "" ? "" : `${prefix}/`;
        const seen = new Map();
        for (const key of this.#files.keys()) {
            if (!key.startsWith(head)) continue;
            const rest = key.slice(head.length);
            if (rest === "") continue;
            const slash = rest.indexOf("/");
            const name = slash === -1 ? rest : rest.slice(0, slash);
            const kind = slash === -1 ? "file" : "directory";
            if (!seen.has(name)) seen.set(name, { name, kind, path: head + name });
        }
        return [...seen.values()].sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0));
    }

    async listDirectories(path) {
        return (await this.listDir(path)).filter((entry) => entry.kind === "directory");
    }

    async listFiles(path) {
        return (await this.listDir(path)).filter((entry) => entry.kind === "file");
    }

    async stat(path) {
        const file = this.#files.get(normalize(path));
        if (file === undefined) return null;
        return { path: normalize(path), size: file.size, lastModified: file.lastModified };
    }

    async file(path) {
        return this.#files.get(normalize(path)) ?? null;
    }

    async readFile(path) {
        const file = await this.file(path);
        return file === null ? null : new Uint8Array(await file.arrayBuffer());
    }

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

    async objectUrl(path) {
        const file = await this.file(path);
        return file === null ? null : URL.createObjectURL(file);
    }

    // Same contract as HandleFs.walk: `filter` selects files, `prune` skips directories. There is no
    // tree to stop descending here, so pruning is answered by testing each of a file's ancestors — the
    // result has to match what a real traversal would have produced, not merely be cheap.
    async walk(path = "", { filter = null, prune = null, maxEntries = Infinity } = {}) {
        const root = normalize(path);
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
