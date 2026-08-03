// The assertions, run inside Chromium by run.mjs. Everything here exercises the shipping modules in
// web/lib; nothing is re-implemented for the test.

import { HandleFs } from "../lib/handle-fs.js";
import { ListingFs } from "../lib/listing-fs.js";
import { parseDatTopLevel } from "../lib/dat.js";
import { baseName, dirName, join, normalize, segments } from "../lib/paths.js";
import { PickKind, compareForMenu, probeInstall } from "../lib/catalog.js";

const results = [];

function check(name, condition, detail = "") {
    results.push({ name, ok: Boolean(condition), detail: condition ? "" : detail });
}

function equal(name, actual, expected) {
    const ok = Object.is(actual, expected);
    check(name, ok, ok ? "" : `expected ${JSON.stringify(expected)}, got ${JSON.stringify(actual)}`);
}

// --- OPFS seeding ---------------------------------------------------------------------------------

async function seed(manifest, prefix) {
    const opfs = await navigator.storage.getDirectory();
    // A fresh subtree per run: OPFS survives between navigations within the origin.
    await opfs.removeEntry(prefix, { recursive: true }).catch(() => {});
    const root = await opfs.getDirectoryHandle(prefix, { create: true });

    for (const entry of manifest.entries) {
        const parts = entry.path.split("/");
        let directory = root;
        for (const part of parts.slice(0, -1)) {
            directory = await directory.getDirectoryHandle(part, { create: true });
        }
        const file = await directory.getFileHandle(parts[parts.length - 1], { create: true });
        const writable = await file.createWritable();
        if (entry.data !== null) await writable.write(base64ToBytes(entry.data));
        await writable.close();
    }
    return root;
}

function base64ToBytes(base64) {
    const binary = atob(base64);
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
    return bytes;
}

// The webkitdirectory fallback takes a FileList whose entries carry a webkitRelativePath rooted at the
// picked folder's name. Real File objects with that property shadowed give ListingFs exactly what a
// browser would hand it.
function fileListFrom(manifest, rootName) {
    return manifest.entries.map((entry) => {
        const bytes = entry.data === null ? new Uint8Array(0) : base64ToBytes(entry.data);
        const file = new File([bytes], entry.path.split("/").pop());
        Object.defineProperty(file, "webkitRelativePath", { value: `${rootName}/${entry.path}` });
        return file;
    });
}

// --- The suite ------------------------------------------------------------------------------------

export async function runSuite(manifest) {
    results.length = 0;

    paths();
    dat();
    await installLayout(manifest);
    await steamLibraryLayout(manifest);
    await fallbackParity(manifest);
    await rangeReads(manifest);

    return results;
}

function paths() {
    equal("normalize collapses separators", normalize("Maps//PEI\\Level.dat"), "Maps/PEI/Level.dat");
    equal("normalize drops leading slash", normalize("/Maps/PEI/"), "Maps/PEI");
    equal("normalize resolves .", normalize("Maps/./PEI"), "Maps/PEI");
    equal("normalize cannot escape the root", normalize("../../etc/passwd"), "etc/passwd");
    equal("normalize walks .. within the subtree", normalize("Maps/PEI/../Washington"), "Maps/Washington");
    equal("baseName", baseName("Maps/PEI/Level.dat"), "Level.dat");
    equal("dirName", dirName("Maps/PEI/Level.dat"), "Maps/PEI");
    equal("join", join("Maps", "PEI/", "/Level.dat"), "Maps/PEI/Level.dat");
    equal("segments count", segments("/a/b/c/").length, 3);
}

function dat() {
    const values = parseDatTopLevel(
        [
            "/ a comment line",
            "Name Prince Edward Island",
            "Description Sunny island off the East coast of Canada.",
            "Nested",
            "{",
            "    Name Not this one",
            "}",
            'Quoted_Key "quoted value"',
        ].join("\n"),
    );
    equal("dat reads a top-level key", values.get("Name"), "Prince Edward Island");
    equal(
        "dat keeps spaces in a value",
        values.get("Description"),
        "Sunny island off the East coast of Canada.",
    );
    check("dat skips nested blocks", values.get("Name") === "Prince Edward Island", "nested Name leaked out");
    equal("dat unquotes values", values.get("Quoted_Key"), "quoted value");
}

async function installLayout(manifest) {
    const root = await seed(manifest, "install");
    const fs = new HandleFs(root);
    const result = await probeInstall(fs, { platform: "linux" });

    equal("probe recognises the install", result.kind, PickKind.Install);
    check("probe reports ok", result.ok, result.reason ?? "");
    equal("install path is the picked folder", result.installPath, "");
    equal("master bundle found", result.masterBundle?.path, "Bundles/core_linux.masterbundle");
    check(
        "workshop content is outside the grant",
        result.workshopPath === null && result.workshopReachable === false,
        `workshopPath=${result.workshopPath}`,
    );

    const pei = result.maps.find((map) => map.folderName === "PEI");
    check("PEI is listed", pei !== undefined, `maps: ${result.maps.map((m) => m.folderName).join(", ")}`);
    if (pei === undefined) return;

    equal("PEI is playable", pei.supported, true);
    equal("PEI tile count", pei.tileCount, 16);
    equal("PEI spans 4 tiles per side", pei.sizeMetres, 4096);
    equal("PEI category comes from Config.json", pei.category, "Official");
    equal("PEI source", pei.source, "Official");
    check(
        "PEI blurb comes from English.dat",
        typeof pei.description === "string" && pei.description.startsWith("Sunny island off the East coast"),
        `description=${JSON.stringify(pei.description)}`,
    );
    check("PEI artwork resolved", pei.previewPath === "Maps/PEI/Preview.png", `preview=${pei.previewPath}`);

    // The maps the probe lists are exactly the folders that have a Level.dat.
    const folders = await fs.listDirectories("Maps");
    const withLevel = [];
    for (const folder of folders) {
        if (await fs.isFile(`${folder.path}/Level.dat`)) withLevel.push(folder.name);
    }
    equal("every Level.dat folder is listed", result.maps.length, withLevel.length);

    // Ordering: playable before unplayable, official before workshop.
    const unplayable = { supported: false, source: "Official", displayName: "Z" };
    const playable = { supported: true, source: "Workshop", displayName: "A" };
    check("playable maps sort first", compareForMenu(playable, unplayable) < 0, "sort order wrong");
}

async function steamLibraryLayout(manifest) {
    // The same content one level down, where a Steam library keeps it, plus an empty workshop folder.
    const nested = {
        entries: [
            ...manifest.entries.map((entry) => ({
                ...entry,
                path: `steamapps/common/Unturned/${entry.path}`,
            })),
            { path: "steamapps/workshop/content/304930/.keep", size: 0, data: "" },
        ],
    };
    const root = await seed(nested, "library");
    const result = await probeInstall(new HandleFs(root), { platform: "linux" });

    equal("probe recognises a Steam library", result.kind, PickKind.SteamLibrary);
    equal("install path points into the library", result.installPath, "steamapps/common/Unturned");
    equal("workshop content is inside the grant", result.workshopPath, "steamapps/workshop/content/304930");
    check(
        "maps are still found one level down",
        result.maps.some((map) => map.folderName === "PEI"),
        `maps: ${result.maps.map((m) => m.folderName).join(", ")}`,
    );
}

// The fallback for browsers with no directory picker has to see the same install as the real thing.
async function fallbackParity(manifest) {
    const listing = new ListingFs(fileListFrom(manifest, "Unturned"));
    const viaListing = await probeInstall(listing, { platform: "linux" });

    const root = await seed(manifest, "install");
    const viaHandles = await probeInstall(new HandleFs(root), { platform: "linux" });

    equal("fallback keeps the folder name", listing.name, "Unturned");
    equal("fallback recognises the install", viaListing.kind, viaHandles.kind);
    equal("fallback finds the same bundle", viaListing.masterBundle?.path, viaHandles.masterBundle?.path);
    equal("fallback lists the same maps", viaListing.maps.length, viaHandles.maps.length);
    equal(
        "fallback reads the same map metadata",
        JSON.stringify(viaListing.maps.map((m) => [m.folderName, m.tileCount, m.category, m.description])),
        JSON.stringify(viaHandles.maps.map((m) => [m.folderName, m.tileCount, m.category, m.description])),
    );
}

// Range reads are how anything large gets read here — a 1.4 GB masterbundle has no whole-file read in a
// 32-bit wasm heap. Level.dat is small, but the mechanism is the same one the bundle streamer needs.
async function rangeReads(manifest) {
    const root = await seed(manifest, "install");
    const fs = new HandleFs(root);

    const whole = await fs.readFile("Maps/PEI/Level.dat");
    check("readFile returns bytes", whole instanceof Uint8Array && whole.length > 0, `len=${whole?.length}`);

    const head = await fs.readRange("Maps/PEI/Level.dat", 0, 4);
    equal("readRange honours the length", head.length, 4);
    check(
        "readRange matches the whole file",
        head.every((byte, index) => byte === whole[index]),
        "prefix mismatch",
    );

    const past = await fs.readRange("Maps/PEI/Level.dat", whole.length + 10, 16);
    equal("readRange past the end is empty", past.length, 0);

    const clamped = await fs.readRange("Maps/PEI/Level.dat", whole.length - 2, 100);
    equal("readRange clamps to the file size", clamped.length, 2);

    equal("stat reports the real size", (await fs.stat("Maps/PEI/Level.dat")).size, whole.length);
    equal("missing files stat as null", await fs.stat("Maps/PEI/Nope.dat"), null);
    equal("missing files read as null", await fs.readFile("Maps/PEI/Nope.dat"), null);
    equal("listing a missing directory is empty", (await fs.listDir("Nope")).length, 0);

    const walked = await fs.walk("Maps/PEI/Landscape/Heightmaps");
    equal("walk finds every tile", walked.length, 16);
}

globalThis.runSuite = runSuite;
