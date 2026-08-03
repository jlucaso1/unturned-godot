// What both filesystem backends have in common.
//
// HandleFs and ListingFs are advertised as interchangeable, and the install probe is written against
// that promise. Most of the interface is not really per-backend though: once a backend can answer
// `file(path)` and `listDir(path)`, reading bytes, ranges, text, an object URL and the filtered listings
// all follow identically. Keeping two copies of those meant a change applied to one and not the other
// would produce exactly the parity break the interface exists to prevent — so they live here once, and
// each backend implements only what it actually does differently: `file`, `listDir`, `stat`, `walk`,
// `invalidate` and its own construction.

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

    async readText(path) {
        const file = await this.file(path);
        return file === null ? null : file.text();
    }

    // A blob: URL for an <img>. The caller owns it and must URL.revokeObjectURL it.
    async objectUrl(path) {
        const file = await this.file(path);
        return file === null ? null : URL.createObjectURL(file);
    }
}
