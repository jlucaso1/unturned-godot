// What both filesystem backends have in common.
//
// HandleFs and ListingFs are advertised as interchangeable, and the install probe is written against
// that promise. Most of the interface is not really per-backend though: once a backend can answer
// `file(path)` and `listDir(path)`, reading bytes, ranges, text, an object URL and the filtered listings
// all follow identically. Keeping two copies of those meant a change applied to one and not the other
// would produce exactly the parity break the interface exists to prevent — so they live here once, and
// each backend implements only what it actually does differently: `file`, `listDir`, `stat`, `walk`,
// `invalidate` and its own construction.

// The encodings File.ReadAllText recognises from a byte-order mark. Anything without one is UTF-8,
// which is what every file the game ships actually is; this is for the hand-edited exceptions.
export function decodeText(bytes) {
    if (bytes.length >= 3 && bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf) {
        return new TextDecoder("utf-8").decode(bytes.subarray(3));
    }
    if (bytes.length >= 2 && bytes[0] === 0xff && bytes[1] === 0xfe) {
        return new TextDecoder("utf-16le").decode(bytes.subarray(2));
    }
    if (bytes.length >= 2 && bytes[0] === 0xfe && bytes[1] === 0xff) {
        return new TextDecoder("utf-16be").decode(bytes.subarray(2));
    }
    return new TextDecoder("utf-8").decode(bytes);
}

export class ReadOnlyFs {
    // Subclasses must provide:
    //   file(path)     -> File | null
    //   listDir(path)  -> [{ name, kind, path }]
    //   stat(path)     -> { path, size, lastModified } | null

    async listDirectories(path) {
        return (await this.listDir(path)).filter((entry) => entry.kind === "directory");
    }

    async listFiles(path) {
        return (await this.listDir(path)).filter((entry) => entry.kind === "file");
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

    // Decoded by byte-order mark, not always as UTF-8. `File.text()` is UTF-8 only, while the desktop's
    // File.ReadAllText detects a BOM and picks the encoding from it — so an English.dat a map author
    // saved as UTF-16 (which Windows editors did by default for years) reads fine there and would come
    // back here as replacement characters and embedded NULs, losing the map's name and blurb.
    async readText(path) {
        const file = await this.file(path);
        if (file === null) return null;
        return decodeText(new Uint8Array(await file.arrayBuffer()));
    }

    // A blob: URL for an <img>. The caller owns it and must URL.revokeObjectURL it.
    async objectUrl(path) {
        const file = await this.file(path);
        return file === null ? null : URL.createObjectURL(file);
    }
}
