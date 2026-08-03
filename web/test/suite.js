// The assertions, run inside Chromium by run.mjs. Everything here exercises the shipping modules in
// web/lib; nothing is re-implemented for the test.

import { HandleFs } from "../lib/handle-fs.js";
import { ListingFs } from "../lib/listing-fs.js";
import { parseDatTopLevel } from "../lib/dat.js";
import { baseName, dirName, join, normalize, segments } from "../lib/paths.js";
import { PickKind, compareForMenu, probeInstall } from "../lib/catalog.js";
import { forgetHandle, loadHandle, saveHandle } from "../lib/handle-store.js";

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
        return fileFor(entry.path, rootName, bytes);
    });
}

function fileFor(path, rootName, contents) {
    const file = new File([contents], path.split("/").pop());
    Object.defineProperty(file, "webkitRelativePath", { value: `${rootName}/${path}` });
    return file;
}

// A hand-built install, for the layouts no real download contains: same-named workshop maps, tiles named
// the way the engine will not accept, a map folder that cannot be read. ListingFs is a real
// implementation of the filesystem interface, so the probe runs against it unmodified.
function syntheticFs(tree, rootName = "Unturned", options = {}) {
    return new ListingFs(
        Object.entries(tree).map(([path, contents]) => fileFor(path, rootName, contents ?? "")),
        // Case folding is pinned rather than inherited from the user agent, so the suite asserts the
        // same thing on every runner.
        { caseInsensitive: false, ...options },
    );
}

// Wraps a filesystem so one directory listing fails the way a removed folder or a revoked permission
// would, leaving every other read working.
function withUnreadableDirectory(fs, unreadable) {
    return new Proxy(fs, {
        get(target, property) {
            const value = Reflect.get(target, property);
            if (property !== "listFiles" && property !== "listDir" && property !== "listDirectories") {
                return typeof value === "function" ? value.bind(target) : value;
            }
            return async (path, ...rest) => {
                if (String(path) === unreadable) throw new DOMException("simulated", "NotReadableError");
                return value.call(target, path, ...rest);
            };
        },
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
    await tileNaming();
    await relaxedConfigJson();
    menuOrdering();
    await caseFolding();
    await blankLocalizedName();
    await handlePersistence();
    await workshopNameCollisions();
    await unreadableMapIsolation();
    await walkParity(manifest);

    return results;
}

// A map folder with the given tiles, plus the files every map has.
function mapTree(prefix, tiles) {
    const tree = { [`${prefix}/Level.dat`]: "", [`${prefix}/English.dat`]: "Name A Map" };
    for (const tile of tiles) tree[`${prefix}/Landscape/Heightmaps/${tile}`] = "";
    return tree;
}

// LevelInfo.TileRegex accepts only Tile_<x>_<y>_Source.heightmap. Counting anything else would advertise
// a map as playable that the desktop cannot load a single tile of.
async function tileNaming() {
    const accepted = await probeInstall(
        syntheticFs({
            "Bundles/core_linux.masterbundle": "",
            ...mapTree("Maps/Good", ["Tile_0_0_Source.heightmap", "Tile_-1_2_Source.heightmap"]),
        }),
        { platform: "linux" },
    );
    equal("engine-named tiles count", accepted.maps[0]?.tileCount, 2);
    equal("engine-named tiles make a map playable", accepted.maps[0]?.supported, true);

    const rejected = await probeInstall(
        syntheticFs({
            "Bundles/core_linux.masterbundle": "",
            ...mapTree("Maps/Bad", [
                "Tile_0_0.heightmap",
                "tile_1_1_source.heightmap",
                "Tile_x_y_Source.heightmap",
            ]),
        }),
        { platform: "linux" },
    );
    equal("suffixless tiles do not count", rejected.maps[0]?.tileCount, 0);
    equal("a map with no engine-readable tiles is not playable", rejected.maps[0]?.supported, false);

    // int.TryParse rejects a coordinate that overflows, and so does the desktop's tile enumeration.
    const overflowing = await probeInstall(
        syntheticFs({
            "Bundles/core_linux.masterbundle": "",
            ...mapTree("Maps/Huge", [
                "Tile_2147483648_0_Source.heightmap",
                "Tile_0_-2147483649_Source.heightmap",
                "Tile_2147483647_0_Source.heightmap",
            ]),
        }),
        { platform: "linux" },
    );
    equal("out-of-range tile coordinates do not count", overflowing.maps[0]?.tileCount, 1);
}

// MapCatalog.ReadCategory parses Config.json with CommentHandling.Skip *and* AllowTrailingCommas, both
// of which JSON.parse rejects. A hand-edited config is the normal case for a workshop map.
async function relaxedConfigJson() {
    const result = await probeInstall(
        syntheticFs({
            "Bundles/core_linux.masterbundle": "",
            "Maps/Relaxed/Level.dat": "",
            "Maps/Relaxed/Config.json": [
                "{",
                "    // the category, hand-edited",
                '    "Category": "Curated",',
                '    "Use_Legacy_Ground": false,',
                "}",
            ].join("\n"),
        }),
        { platform: "linux" },
    );
    equal("a config with comments and a trailing comma still parses", result.maps[0]?.category, "Curated");

    const stringy = await probeInstall(
        syntheticFs({
            "Bundles/core_linux.masterbundle": "",
            "Maps/Stringy/Level.dat": "",
            "Maps/Stringy/Config.json": '{ "Category": "a // b, ", "Other": 1 }',
        }),
        { platform: "linux" },
    );
    equal("relaxing json leaves string contents alone", stringy.maps[0]?.category, "a // b, ");
}

// CompareForMenu sorts with OrdinalIgnoreCase, not locale collation: "Åland" sorts after "Zeta".
function menuOrdering() {
    const order = [
        { supported: true, source: "Official", displayName: "Åland" },
        { supported: true, source: "Official", displayName: "Zeta" },
        { supported: true, source: "Official", displayName: "Alpha" },
    ].sort(compareForMenu);
    equal(
        "maps sort ordinally, not by locale",
        order.map((map) => map.displayName).join(","),
        "Alpha,Zeta,Åland",
    );
}

// MapCatalog.Read falls back to the folder name on IsNullOrWhiteSpace, so a name of spaces must not
// leave the card with a blank heading.
async function blankLocalizedName() {
    const result = await probeInstall(
        syntheticFs({
            "Bundles/core_linux.masterbundle": "",
            "Maps/Riverside/Level.dat": "",
            "Maps/Riverside/English.dat": 'Name "   "',
        }),
        { platform: "linux" },
    );
    equal("a whitespace-only name falls back to the folder", result.maps[0]?.displayName, "Riverside");
}

// HandleFs resolves paths by asking the browser, which asks the OS, so it folds case exactly where the
// platform does. ListingFs resolves out of its own index and has to be told — otherwise it would be the
// only case-sensitive filesystem on a Windows machine, and a workshop map shipping `level.dat` would
// vanish from the fallback while the desktop's File.Exists finds it.
async function caseFolding() {
    const oddlyCased = {
        "Bundles/core_linux.masterbundle": "",
        "Maps/Riverside/level.dat": "",
        "Maps/Riverside/english.dat": "Name Riverside Rush",
        "Maps/Riverside/Landscape/heightmaps/Tile_0_0_Source.heightmap": "",
    };

    const insensitive = await probeInstall(syntheticFs(oddlyCased, "Unturned", { caseInsensitive: true }), {
        platform: "linux",
    });
    equal("a case-insensitive host finds an oddly-cased map", insensitive.maps.length, 1);
    equal("and reads its metadata", insensitive.maps[0]?.displayName, "Riverside Rush");
    equal("and its tiles", insensitive.maps[0]?.tileCount, 1);

    const sensitive = await probeInstall(syntheticFs(oddlyCased, "Unturned", { caseInsensitive: false }), {
        platform: "linux",
    });
    equal("a case-sensitive host does not, matching the desktop there", sensitive.maps.length, 0);

    // Folding must never shadow a file that is really there.
    const both = syntheticFs({ "Maps/M/Level.dat": "exact", "Maps/M/level.dat": "folded" }, "Unturned", {
        caseInsensitive: true,
    });
    equal("an exact spelling wins over a folded one", await both.readText("Maps/M/Level.dat"), "exact");
    equal(
        "and the folded one is still reachable by its own name",
        await both.readText("Maps/M/level.dat"),
        "folded",
    );
}

// IndexedDB reports a request as successful before its transaction commits, so the store has to resolve
// on completion or a caller cannot tell a written handle from one that was rolled back.
async function handlePersistence() {
    await forgetHandle();
    equal("nothing is stored to begin with", await loadHandle(), null);

    const root = await navigator.storage.getDirectory();
    await saveHandle(root);
    const restored = await loadHandle();
    check("a saved handle comes back", restored !== null, "loadHandle returned null");
    check(
        "and is the same entry",
        restored !== null && (await restored.isSameEntry(root)),
        "restored handle is a different directory",
    );

    await forgetHandle();
    equal("forgetting removes it", await loadHandle(), null);
}

// Folder names are not unique across Steam workshop items, so two subscribed maps that both use one are
// two maps. Only a *placeholder* claims a name, and a subscribed map that replaces one releases it.
async function workshopNameCollisions() {
    const distinct = await probeInstall(
        syntheticFs({
            "steamapps/common/Unturned/Bundles/core_linux.masterbundle": "",
            ...mapTree("steamapps/workshop/content/304930/111/Ireland", ["Tile_0_0_Source.heightmap"]),
            ...mapTree("steamapps/workshop/content/304930/222/Ireland", ["Tile_0_0_Source.heightmap"]),
        }),
        { platform: "linux" },
    );
    equal("two workshop maps sharing a folder name are both listed", distinct.maps.length, 2);
    check(
        "and keep their own paths",
        new Set(distinct.maps.map((map) => map.path)).size === 2,
        distinct.maps.map((map) => map.path).join(", "),
    );

    // An unsupported official folder is a stand-in: the first subscribed map replaces it, and the second
    // is still its own entry rather than being folded into the first.
    const replaced = await probeInstall(
        syntheticFs({
            "steamapps/common/Unturned/Bundles/core_linux.masterbundle": "",
            ...mapTree("steamapps/common/Unturned/Maps/Ireland", []),
            ...mapTree("steamapps/workshop/content/304930/111/Ireland", ["Tile_0_0_Source.heightmap"]),
            ...mapTree("steamapps/workshop/content/304930/222/Ireland", ["Tile_0_0_Source.heightmap"]),
        }),
        { platform: "linux" },
    );
    equal("a placeholder is replaced, not stacked", replaced.maps.length, 2);
    check(
        "and the replacement releases the name",
        replaced.maps.every((map) => map.supported && map.source === "Workshop"),
        replaced.maps.map((map) => `${map.path}:${map.supported}`).join(", "),
    );
}

// One unreadable map folder is one missing entry, not a folder that failed to open — LevelInfo's own
// catch turns an unlistable Heightmaps directory into zero tiles.
async function unreadableMapIsolation() {
    const fs = withUnreadableDirectory(
        syntheticFs({
            "Bundles/core_linux.masterbundle": "",
            ...mapTree("Maps/Broken", ["Tile_0_0_Source.heightmap"]),
            ...mapTree("Maps/Fine", ["Tile_0_0_Source.heightmap"]),
        }),
        "Maps/Broken/Landscape/Heightmaps",
    );

    let result;
    try {
        result = await probeInstall(fs, { platform: "linux" });
    } catch (error) {
        check("an unreadable map does not abort the scan", false, String(error));
        return;
    }

    check("an unreadable map does not abort the scan", true);
    equal("both maps are still listed", result.maps.length, 2);
    equal(
        "the readable map stays playable",
        result.maps.find((m) => m.folderName === "Fine")?.supported,
        true,
    );
    equal(
        "the unreadable map is marked unplayable",
        result.maps.find((m) => m.folderName === "Broken")?.supported,
        false,
    );
}

// The two backends are advertised as interchangeable, so walk has to mean the same thing in both:
// `filter` selects files, `prune` stops a directory from being descended into.
async function walkParity(manifest) {
    const root = await seed(manifest, "install");
    const handles = new HandleFs(root);
    const listing = new ListingFs(fileListFrom(manifest, "Unturned"), { caseInsensitive: false });

    const isDat = (entry) => entry.name.endsWith(".dat");
    const isLandscape = (entry) => entry.name === "Landscape";

    for (const [name, options] of [
        ["unfiltered", {}],
        ["with a file filter", { filter: isDat }],
        ["with a pruned directory", { prune: isLandscape }],
        ["with both", { filter: isDat, prune: isLandscape }],
    ]) {
        const viaHandles = (await handles.walk("Maps", options)).sort();
        const viaListing = (await listing.walk("Maps", options)).sort();
        equal(`walk agrees across backends ${name}`, viaListing.join("|"), viaHandles.join("|"));
    }

    const filtered = await handles.walk("Maps", { filter: isDat });
    check(
        "a file filter selects files rather than pruning",
        filtered.length > 0 && filtered.every((path) => path.endsWith(".dat")),
        `${filtered.length} results`,
    );
    const pruned = await handles.walk("Maps", { prune: isLandscape });
    check(
        "a directory prune drops that subtree",
        pruned.length > 0 && !pruned.some((path) => path.includes("/Landscape/")),
        `${pruned.length} results`,
    );
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

    // The three rules copied from DatParser rather than approximated. Each of these was wrong once, and
    // each way of being wrong shows the browser a different map than the catalogue does.

    // ReadStringValue takes the rest of the line: '/' only opens a comment where a token would start.
    equal(
        "dat keeps slashes inside a value",
        parseDatTopLevel("Description See https://example.com/maps for more").get("Description"),
        "See https://example.com/maps for more",
    );
    equal(
        "dat still honours a whole-line comment",
        parseDatTopLevel(["   / not a key", "Name Kept"].join("\n")).get("Name"),
        "Kept",
    );

    // DatDictionary compares keys with OrdinalIgnoreCase, and the last spelling wins.
    equal("dat matches keys case-insensitively", parseDatTopLevel("nAmE Cased").get("Name"), "Cased");
    equal(
        "dat keeps the last duplicate key",
        parseDatTopLevel(["Name First", "NAME Second"].join("\n")).get("name"),
        "Second",
    );

    // DatParser.Unescape: n and t become control characters, everything else keeps the escaped char.
    equal(
        "dat decodes escapes in an unquoted value",
        parseDatTopLevel("Description One\\nTwo\\tThree\\\\Four").get("Description"),
        "One\nTwo\tThree\\Four",
    );
    equal(
        "dat decodes escapes in a quoted value",
        parseDatTopLevel('Name "a \\"quoted\\" name"').get("Name"),
        'a "quoted" name',
    );

    // ReadQuoted swallows one comma directly after a closing quote, so a quoted key can be followed by
    // one. A space before the comma leaves it in the value, in the desktop parser and here alike.
    equal(
        "dat consumes a comma after a quoted key",
        parseDatTopLevel('"Name", "Map Name"').get("Name"),
        "Map Name",
    );
    equal(
        "dat reads a quoted key with no comma",
        parseDatTopLevel('"Name" "Map Name"').get("Name"),
        "Map Name",
    );
    equal(
        "dat keeps a detached comma in the value",
        parseDatTopLevel('"Name" , "Map Name"').get("Name"),
        ', "Map Name"',
    );

    // `Key {` reads as a key whose value is a brace, so its contents are not top-level either.
    equal(
        "dat skips an inline-brace block",
        parseDatTopLevel(["Nested {", "    Name Leak", "}", "Name Kept"].join("\n")).get("Name"),
        "Kept",
    );
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
    const listing = new ListingFs(fileListFrom(manifest, "Unturned"), { caseInsensitive: false });
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
