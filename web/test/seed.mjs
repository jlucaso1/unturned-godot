// Builds the manifest the browser harness seeds its origin-private file system from.
//
// The point of seeding OPFS rather than mocking anything is that navigator.storage.getDirectory() hands
// back a real FileSystemDirectoryHandle — the very type showDirectoryPicker() returns. So the test drives
// HandleFs down exactly the code path a player's pick takes, against exactly the files the game ships. The
// one thing it cannot drive is the native picker dialog itself, which no automation can click.
//
// File contents are inlined only up to `MAX_INLINE`: what the probe reads (Level.dat, English.dat,
// Config.json, MasterBundle.dat) is a few KB, while what it only counts or checks for (heightmap tiles,
// splatmaps, artwork, the masterbundle) can be hundreds of MB. Those are seeded as empty files, so their
// names and paths are real and their sizes are not.

import { readFile, readdir, stat } from "node:fs/promises";
import { join, relative, sep } from "node:path";

const MAX_INLINE = 256 * 1024;

// `selection` decides what is collected, and it decides it *during* the walk rather than after: an
// Unturned install is mostly Bundles/, none of which any assertion here touches, and reading it only to
// throw it away would base64-encode tens of thousands of files for nothing. `keep` selects files and
// `descend` selects directories, the same split HandleFs.walk uses for the same reason.
export async function buildManifest(root, selection = {}) {
    const entries = [];
    await walk(root, root, entries, selection);
    entries.sort((a, b) => (a.path < b.path ? -1 : 1));
    return { root, entries };
}

async function walk(root, directory, entries, selection) {
    for (const entry of await readdir(directory, { withFileTypes: true })) {
        const absolute = join(directory, entry.name);
        const path = relative(root, absolute).split(sep).join("/");
        if (entry.isDirectory()) {
            if (selection.descend === undefined || selection.descend(path)) {
                await walk(root, absolute, entries, selection);
            }
        } else if (entry.isFile()) {
            if (selection.keep !== undefined && !selection.keep(path)) continue;
            const { size } = await stat(absolute);
            entries.push({
                path,
                size,
                data: size <= MAX_INLINE ? (await readFile(absolute)).toString("base64") : null,
            });
        }
    }
}

// The subset worth shipping to the browser: every map file (the probe walks all of Maps/) plus the
// Bundles/ entries it looks for.
export const PROBE_SELECTION = {
    keep: (path) =>
        path.startsWith("Maps/") ||
        path === "Bundles/MasterBundle.dat" ||
        /^Bundles\/core(_linux|_mac)?\.masterbundle$/.test(path),
    // Bundles/ itself has to be entered for MasterBundle.dat and the masterbundle, but nothing below it.
    descend: (path) => path === "Maps" || path.startsWith("Maps/") || path === "Bundles",
};
