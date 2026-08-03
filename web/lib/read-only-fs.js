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
//
// Every decoder below is built with `ignoreBOM: true`, which reads backwards: it means "do not treat a
// leading U+FEFF as a mark", i.e. keep it. The signature has already been sliced off by then, so this
// only matters when a file carries *two* — and .NET removes exactly the one it detected the encoding
// from, leaving the second as content. Without the flag the decoder would silently eat that one too, and
// a Config.json the desktop rejects as malformed JSON would parse here.
export function decodeText(bytes) {
    if (bytes.length >= 3 && bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf) {
        return new TextDecoder("utf-8", { ignoreBOM: true }).decode(bytes.subarray(3));
    }
    // UTF-32 first: a UTF-32LE file starts FF FE 00 00, whose first two bytes are exactly the UTF-16LE
    // mark. Checking the shorter one first would decode it as UTF-16 and hand back NUL-interleaved text
    // that no parser here can read, where File.ReadAllText reads it correctly.
    if (
        bytes.length >= 4 &&
        bytes[0] === 0xff &&
        bytes[1] === 0xfe &&
        bytes[2] === 0x00 &&
        bytes[3] === 0x00
    ) {
        return decodeUtf32(bytes.subarray(4), true);
    }
    if (
        bytes.length >= 4 &&
        bytes[0] === 0x00 &&
        bytes[1] === 0x00 &&
        bytes[2] === 0xfe &&
        bytes[3] === 0xff
    ) {
        return decodeUtf32(bytes.subarray(4), false);
    }
    if (bytes.length >= 2 && bytes[0] === 0xff && bytes[1] === 0xfe) {
        return new TextDecoder("utf-16le", { ignoreBOM: true }).decode(bytes.subarray(2));
    }
    if (bytes.length >= 2 && bytes[0] === 0xfe && bytes[1] === 0xff) {
        return new TextDecoder("utf-16be", { ignoreBOM: true }).decode(bytes.subarray(2));
    }
    return new TextDecoder("utf-8").decode(bytes);
}

// TextDecoder has no UTF-32: the Encoding standard dropped it, so it is decoded by hand. Four bytes per
// code point, and anything outside Unicode's range becomes the replacement character rather than a throw
// — a map with one bad code point should lose that character, not its whole name.
//
// A trailing one to three bytes are a code unit the file was cut off in the middle of. .NET's decoder
// emits one replacement character for that tail rather than dropping it, and TextDecoder does the same
// for an odd byte at the end of a UTF-16 file, so dropping it here would have been the only quiet
// truncation among the encodings — the desktop reading `Na�` where the browser read `Na`.
function decodeUtf32(bytes, littleEndian) {
    const whole = bytes.byteLength - (bytes.byteLength % 4);
    const view = new DataView(bytes.buffer, bytes.byteOffset, whole);
    let out = "";
    for (let i = 0; i < whole; i += 4) {
        const code = view.getUint32(i, littleEndian);
        out += code > 0x10ffff || (code >= 0xd800 && code <= 0xdfff) ? "�" : String.fromCodePoint(code);
    }
    return whole === bytes.byteLength ? out : `${out}�`;
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
