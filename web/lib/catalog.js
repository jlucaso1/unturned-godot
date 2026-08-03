// Recognises an Unturned install in a folder the player picked, and lists the maps in it.
//
// This mirrors, in the browser, what core/ does on the desktop: UnturnedInstall (where the install and
// its masterbundle are), ContentSource (which folders a bundle owns) and MapCatalog + LevelInfo (which
// map folders exist, what they are called, and how many Landscape tiles they have). It is a probe, not a
// second implementation of the game: it answers "is this the right folder, and what is in it" so the
// player gets an answer in milliseconds instead of after a multi-minute load. The loading itself belongs
// to core/, which is why the shapes below use the same names and the same rules.

import { parseDatTopLevel } from "./dat.js";
import { normalize } from "./paths.js";
import { currentPlatform, isCaseInsensitiveFilesystem } from "./platform.js";

export { currentPlatform };

// core/Data/Landscape.cs
const TILE_SIZE = 1024;
// The game's Steam app id, which is also its workshop content folder.
const WORKSHOP_APP_ID = "304930";
// core/Data/LevelInfo.cs's TileRegex, copied exactly — including its case sensitivity and its refusal of
// the suffixless form. A map holding Tile_0_0.heightmap has no tiles the desktop can load, so counting
// it here would advertise a map as playable that is not.
const TILE_PATTERN = /^Tile_(-?\d+)_(-?\d+)_Source\.heightmap$/;

export const MapSource = Object.freeze({ Official: "Official", Workshop: "Workshop" });

// Where an install sits relative to the folder that was picked. Both are worth supporting: picking the
// Unturned folder is the obvious thing to do, but only picking the Steam *library* puts
// steamapps/workshop/content/304930 inside the granted subtree, and that is where subscribed maps live.
// The File System Access API grants exactly the chosen subtree and nothing above it, so a player who
// picks Unturned/ simply has no workshop maps available — which the UI says out loud rather than
// silently listing fewer maps.
export const PickKind = Object.freeze({
    Install: "install",
    SteamLibrary: "steam-library",
    Unknown: "unknown",
});

// Finds the install root inside whatever was picked. Returns a path relative to the picked folder ("" if
// the folder is itself the install).
export async function locateInstall(fs) {
    if (await isInstallRoot(fs, "")) {
        return { kind: PickKind.Install, installPath: "", workshopPath: null };
    }

    // A Steam library: <library>/steamapps/common/Unturned, with the workshop content beside it.
    const library = "steamapps/common/Unturned";
    if (await isInstallRoot(fs, library)) {
        return {
            kind: PickKind.SteamLibrary,
            installPath: library,
            workshopPath: `steamapps/workshop/content/${WORKSHOP_APP_ID}`,
        };
    }

    return { kind: PickKind.Unknown, installPath: null, workshopPath: null };
}

// An install is a folder with a Bundles/ directory in it. Bundles/ is what holds the masterbundle and the
// object/tree assets, and no other folder in a Steam library looks like that.
async function isInstallRoot(fs, path) {
    return fs.isDirectory(join(path, "Bundles"));
}

// The masterbundle for this install: the variant for the player's platform first, then the others, as
// UnturnedInstall.FindBundle does. Accepting another platform's bundle matters more here than on the
// desktop, since the browser has no say in which OS the folder came from.
export async function findMasterBundle(fs, installPath, platform = currentPlatform()) {
    const bundles = join(installPath, "Bundles");
    for (const suffix of suffixesFor(platform)) {
        const path = join(bundles, `core${suffix}.masterbundle`);
        const stat = await safe(fs.stat(path), null);
        if (stat !== null) return { path, size: stat.size, suffix: suffix || "(windows)" };
    }
    return null;
}

function suffixesFor(platform) {
    if (platform === "windows") return ["", "_linux", "_mac"];
    if (platform === "mac") return ["_mac", "", "_linux"];
    return ["_linux", "", "_mac"];
}

// The directories map folders live in, most authoritative first — MapCatalog.SearchDirectories.
export function searchDirectories({ installPath, workshopPath }) {
    const directories = [
        { path: join(installPath, "Maps"), source: MapSource.Official },
        { path: join(installPath, "Bundles/Workshop/Maps"), source: MapSource.Workshop },
    ];
    if (workshopPath) directories.push({ path: workshopPath, source: MapSource.Workshop });
    return directories;
}

// Reads one map folder, or null when it is not a map. Level.dat is what every map has — same test as
// MapCatalog.Read.
export async function readMap(fs, mapPath, source) {
    if (!(await safe(fs.isFile(join(mapPath, "Level.dat")), false))) return null;

    const folder = normalize(mapPath).split("/").pop() ?? "";
    const localization = await readLocalization(fs, mapPath);
    const { count, sizeMetres } = await measureLandscape(fs, mapPath);

    return {
        folderName: folder,
        path: normalize(mapPath),
        // MapCatalog.Read falls back on IsNullOrWhiteSpace, not on emptiness: a localized name of spaces
        // would otherwise leave the card with a blank heading.
        displayName: localization.name?.trim() ? localization.name : folder,
        source,
        description: localization.description,
        category: await readCategory(fs, mapPath),
        tileCount: count,
        sizeMetres,
        // Pre-2020 maps store terrain as one legacy heightmap instead of Landscape tiles; this port does
        // not read those, so they are listed and marked rather than hidden.
        supported: count > 0,
        iconPath: await existingFile(fs, mapPath, "Icon.png"),
        previewPath: await existingFile(fs, mapPath, "Preview.png"),
        chartPath: await existingFile(fs, mapPath, "Chart.png"),
    };
}

async function existingFile(fs, mapPath, name) {
    const path = join(mapPath, name);
    return (await safe(fs.isFile(path), false)) ? path : null;
}

// One unreadable file must not take down the catalogue with it. Every read in core/ that feeds the map
// browser is already guarded this way — TryReadAllText swallows IOException, LevelInfo.EnumerateTiles
// turns an unlistable directory into zero tiles, SafeEnumerateDirectories into no entries — because a
// map the player cannot read is one missing entry, not a folder that failed to open. A picked directory
// is live: files move, permissions change, and a removable drive can vanish mid-scan.
async function safe(promise, fallback) {
    try {
        return await promise;
    } catch {
        return fallback;
    }
}

async function readLocalization(fs, mapPath) {
    const text = await safe(fs.readText(join(mapPath, "English.dat")), null);
    if (text === null) return { name: null, description: null };
    const values = parseDatTopLevel(text);
    return { name: values.get("Name") ?? null, description: values.get("Description") ?? null };
}

async function readCategory(fs, mapPath) {
    const text = await safe(fs.readText(join(mapPath, "Config.json")), null);
    if (text === null) return null;
    try {
        const config = JSON.parse(relaxJson(text));
        return typeof config?.Category === "string" ? config.Category : null;
    } catch {
        // A map whose config will not parse still loads; the category is cosmetic.
        return null;
    }
}

// Config.json is hand-edited by map authors, and MapCatalog.ReadCategory reads it with both
// CommentHandling.Skip and AllowTrailingCommas. JSON.parse allows neither, so both are taken out first
// — in two string-preserving passes rather than one, because a trailing comma can be separated from its
// bracket by the very comment the other pass removes.
function relaxJson(text) {
    // A removed comment leaves a space behind, not nothing. `{"X":1/*c*/2}` collapsing to `{"X":12}`
    // would turn a document JsonDocument rejects into one JSON.parse accepts — and with a different
    // value, which is worse than failing the way the desktop does.
    const withoutComments = text.replace(
        /("(?:\\.|[^"\\])*")|\/\/[^\n\r]*|\/\*[\s\S]*?\*\//g,
        (match, string) => string ?? " ",
    );
    return withoutComments.replace(/("(?:\\.|[^"\\])*")|,(?=\s*[}\]])/g, (match, string) => string ?? "");
}

// Tile count and the edge of the square they span, in metres — MapCatalog.MeasureLandscape over
// LevelInfo.EnumerateTiles.
async function measureLandscape(fs, mapPath) {
    const entries = await safe(fs.listFiles(join(mapPath, "Landscape/Heightmaps")), []);
    let minX = Infinity;
    let maxX = -Infinity;
    let minY = Infinity;
    let maxY = -Infinity;
    let count = 0;

    for (const entry of entries) {
        const match = TILE_PATTERN.exec(entry.name);
        if (match === null) continue;
        // LevelInfo.EnumerateTiles parses with int.TryParse and skips the tile when it overflows, so a
        // coordinate outside the signed 32-bit range is a tile the desktop will not load either.
        const x = parseTileCoordinate(match[1]);
        const y = parseTileCoordinate(match[2]);
        if (x === null || y === null) continue;
        minX = Math.min(minX, x);
        maxX = Math.max(maxX, x);
        minY = Math.min(minY, y);
        maxY = Math.max(maxY, y);
        count++;
    }

    if (count === 0) return { count: 0, sizeMetres: 0 };
    const span = Math.max(maxX - minX, maxY - minY) + 1;
    return { count, sizeMetres: span * TILE_SIZE };
}

function parseTileCoordinate(digits) {
    const value = Number.parseInt(digits, 10);
    return Number.isSafeInteger(value) && value >= -2147483648 && value <= 2147483647 ? value : null;
}

// The order the menu lists maps in — MapCatalog.CompareForMenu: playable first, official before
// workshop, then by name.
export function compareForMenu(a, b) {
    if (a.supported !== b.supported) return a.supported ? -1 : 1;
    if (a.source !== b.source) return a.source === MapSource.Official ? -1 : 1;
    return compareOrdinalIgnoreCase(a.displayName, b.displayName);
}

// MapCatalog.CompareForMenu sorts with StringComparison.OrdinalIgnoreCase, which compares code units
// after an invariant upcase — not by locale collation. The difference is visible the moment a map's name
// carries an accent: locale rules file "Åland" next to "Aland", ordinal rules file it after "Zeta", and
// the browser is supposed to be previewing the menu the game will show.
function compareOrdinalIgnoreCase(a, b) {
    const left = simpleUpperCase(a);
    const right = simpleUpperCase(b);
    return left < right ? -1 : left > right ? 1 : 0;
}

// The upcase OrdinalIgnoreCase performs is one code point at a time, and a code point whose uppercase
// is longer than itself is left alone: ToUpperInvariant('ß') is 'ß', not "SS". JavaScript's toUpperCase
// does the full Unicode mapping, which would make "ß" and "SS" compare equal here and not on the
// desktop — so anything that expands keeps its original form.
function simpleUpperCase(text) {
    let out = "";
    for (const character of text) {
        const upper = character.toUpperCase();
        out += upper.length === character.length ? upper : character;
    }
    return out;
}

// The whole probe: what was picked, whether it is an install, what content it carries and which maps are
// in it. Everything is best-effort — a folder missing a piece is reported, never thrown over.
export async function probeInstall(fs, { platform = currentPlatform() } = {}) {
    const located = await locateInstall(fs);
    if (located.kind === PickKind.Unknown) {
        return {
            ok: false,
            kind: located.kind,
            reason:
                "No Bundles folder here. Pick the Unturned install itself " +
                "(steamapps/common/Unturned) or the Steam library folder that contains it.",
            rootName: fs.name,
            maps: [],
        };
    }

    const { installPath, workshopPath } = located;
    const masterBundle = await findMasterBundle(fs, installPath, platform);
    const maps = await scanMaps(fs, located, { platform });

    return {
        ok: masterBundle !== null,
        kind: located.kind,
        rootName: fs.name,
        installPath,
        // The workshop content directory is only inside the grant when a Steam library was picked.
        workshopPath:
            workshopPath !== null && (await safe(fs.isDirectory(workshopPath), false)) ? workshopPath : null,
        workshopReachable: workshopPath !== null,
        masterBundle,
        reason:
            masterBundle === null
                ? "Found a Bundles folder but no core*.masterbundle in it: the install looks incomplete."
                : null,
        maps,
    };
}

// MapCatalog.Scan: official folders, the game's own Workshop/Maps copies, and Steam's workshop content,
// with a workshop item's map allowed to sit one folder deeper.
export async function scanMaps(fs, located, { platform = currentPlatform() } = {}) {
    const maps = [];
    const seenPaths = new Set();
    // Folder name -> index of a *placeholder* entry a real subscribed map may replace. Only placeholders
    // ever claim a name, which is the part that matters: folder names are not unique across Steam
    // workshop items, so two subscribed maps both called "Ireland" are two independent maps and both
    // belong in the list. Deduplication is by path.
    const placeholderByName = new Map();
    const bundledWorkshopRoot = join(located.installPath, "Bundles/Workshop/Maps");
    // MapCatalog.IsBundledWorkshopCopy compares this prefix with PathComparison, which folds case on
    // Windows. The fallback backend resolves `bundles/workshop/maps` there, so a case-sensitive test
    // would classify the game's own stale copy as an ordinary workshop map and list it twice.
    const foldPathCase = isCaseInsensitiveFilesystem(platform);
    const bundledPrefix = foldPathCase ? `${bundledWorkshopRoot}/`.toLowerCase() : `${bundledWorkshopRoot}/`;

    for (const { path, source } of searchDirectories(located)) {
        for (const candidate of await safe(fs.listDirectories(path), [])) {
            const entry = await readMap(fs, candidate.path, source);
            if (entry !== null) {
                add(entry);
                continue;
            }
            if (source !== MapSource.Workshop) continue;

            // A workshop item wraps its map in one more folder level, and may hold a mod and no map.
            for (const nested of await safe(fs.listDirectories(candidate.path), [])) {
                const nestedEntry = await readMap(fs, nested.path, source);
                if (nestedEntry !== null) add(nestedEntry);
            }
        }
    }

    maps.sort(compareForMenu);
    return maps;

    // MapCatalog.Scan's Add, including the part that is easy to lose: after a subscribed map replaces a
    // placeholder, the name slot is *released*, so the next item that happens to use the same folder
    // name is listed on its own instead of being folded into the first.
    function add(entry) {
        if (seenPaths.has(entry.path)) return;
        seenPaths.add(entry.path);

        // An unsupported official folder, and the game's own copy under Bundles/Workshop/Maps, can both
        // be stale stand-ins for a map the player is actually subscribed to.
        const bundledCopy = (foldPathCase ? entry.path.toLowerCase() : entry.path).startsWith(bundledPrefix);
        const placeholder = bundledCopy || (entry.source === MapSource.Official && !entry.supported);
        const key = entry.folderName.toLowerCase();
        const index = placeholderByName.get(key);

        if (index === undefined) {
            if (placeholder) placeholderByName.set(key, maps.length);
            maps.push(entry);
        } else if (entry.source === MapSource.Workshop && !bundledCopy) {
            maps[index] = entry;
            placeholderByName.delete(key);
        } else if (entry.supported && !maps[index].supported) {
            maps[index] = entry;
        }
    }
}

function join(...parts) {
    return normalize(parts.filter((part) => part !== null && part !== "").join("/"));
}
