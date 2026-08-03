// Path helpers for the browser-side file layer.
//
// Every path the probe and the future engine bridge use is POSIX-shaped and relative to the folder the
// player picked: "Maps/PEI/Level.dat", never "C:\...\Unturned\Maps\PEI\Level.dat". The File System Access
// API hands out one handle per path segment and has no notion of an absolute path at all, so normalizing
// here keeps every caller from re-deciding what a separator is.

// Splits a path into its segments, tolerating either separator, repeats, and "." — Unturned's own data
// files mix "/" and "\" freely (Level.hierarchy asset paths, MasterBundle.dat prefixes).
export function segments(path) {
    const parts = [];
    for (const raw of String(path ?? "").split(/[/\\]+/)) {
        if (raw === "" || raw === ".") continue;
        if (raw === "..") {
            // A picked directory is the root of its own world: there is no handle above it, so ".."
            // cannot escape and is dropped rather than silently resolving somewhere unexpected.
            parts.pop();
            continue;
        }
        parts.push(raw);
    }
    return parts;
}

// The canonical form the handle caches are keyed on. The root is "".
export function normalize(path) {
    return segments(path).join("/");
}

export function join(...parts) {
    return normalize(parts.join("/"));
}

// "Maps/PEI/Level.dat" -> "Level.dat"
export function baseName(path) {
    const parts = segments(path);
    return parts.length === 0 ? "" : parts[parts.length - 1];
}

// "Maps/PEI/Level.dat" -> "Maps/PEI"
export function dirName(path) {
    const parts = segments(path);
    parts.pop();
    return parts.join("/");
}
