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

// core/Data/Landscape.cs
const TILE_SIZE = 1024;
// The game's Steam app id, which is also its workshop content folder.
const WORKSHOP_APP_ID = "304930";
const TILE_PATTERN = /^Tile_(-?\d+)_(-?\d+)(?:_Source)?\.heightmap$/i;

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
        const stat = await fs.stat(path);
        if (stat !== null) return { path, size: stat.size, suffix: suffix || "(windows)" };
    }
    return null;
}

function suffixesFor(platform) {
    if (platform === "windows") return ["", "_linux", "_mac"];
    if (platform === "mac") return ["_mac", "", "_linux"];
    return ["_linux", "", "_mac"];
}

// The player's OS as the bundle naming sees it, from the UA. Only used to order the candidates, so a
// wrong guess costs one extra lookup and nothing else.
export function currentPlatform() {
    const ua = globalThis.navigator?.userAgent ?? "";
    if (/Windows/i.test(ua)) return "windows";
    if (/Mac OS X|Macintosh/i.test(ua)) return "mac";
    return "linux";
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
    if (!(await fs.isFile(join(mapPath, "Level.dat")))) return null;

    const folder = normalize(mapPath).split("/").pop() ?? "";
    const localization = await readLocalization(fs, mapPath);
    const { count, sizeMetres } = await measureLandscape(fs, mapPath);

    return {
        folderName: folder,
        path: normalize(mapPath),
        displayName: localization.name || folder,
        source,
        description: localization.description,
        category: await readCategory(fs, mapPath),
        tileCount: count,
        sizeMetres,
        // Pre-2020 maps store terrain as one legacy heightmap instead of Landscape tiles; this port does
        // not read those, so they are listed and marked rather than hidden.
        supported: count > 0,
        iconPath: (await fs.isFile(join(mapPath, "Icon.png"))) ? join(mapPath, "Icon.png") : null,
        previewPath: (await fs.isFile(join(mapPath, "Preview.png"))) ? join(mapPath, "Preview.png") : null,
        chartPath: (await fs.isFile(join(mapPath, "Chart.png"))) ? join(mapPath, "Chart.png") : null,
    };
}

async function readLocalization(fs, mapPath) {
    const text = await fs.readText(join(mapPath, "English.dat"));
    if (text === null) return { name: null, description: null };
    const values = parseDatTopLevel(text);
    return { name: values.get("Name") ?? null, description: values.get("Description") ?? null };
}

async function readCategory(fs, mapPath) {
    const text = await fs.readText(join(mapPath, "Config.json"));
    if (text === null) return null;
    try {
        const config = JSON.parse(stripJsonComments(text));
        return typeof config?.Category === "string" ? config.Category : null;
    } catch {
        // A map whose config will not parse still loads; the category is cosmetic.
        return null;
    }
}

// Config.json is hand-edited by map authors and the game's reader tolerates // comments, so JSON.parse
// gets the same courtesy MapCatalog gives it via JsonCommentHandling.Skip.
function stripJsonComments(text) {
    return text.replace(
        /("(?:\\.|[^"\\])*")|\/\/[^\n\r]*|\/\*[\s\S]*?\*\//g,
        (match, string) => string ?? "",
    );
}

// Tile count and the edge of the square they span, in metres — MapCatalog.MeasureLandscape over
// LevelInfo.EnumerateTiles.
async function measureLandscape(fs, mapPath) {
    const entries = await fs.listFiles(join(mapPath, "Landscape/Heightmaps"));
    let minX = Infinity;
    let maxX = -Infinity;
    let minY = Infinity;
    let maxY = -Infinity;
    let count = 0;

    for (const entry of entries) {
        const match = TILE_PATTERN.exec(entry.name);
        if (match === null) continue;
        const x = Number.parseInt(match[1], 10);
        const y = Number.parseInt(match[2], 10);
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

// The order the menu lists maps in — MapCatalog.CompareForMenu: playable first, official before
// workshop, then by name.
export function compareForMenu(a, b) {
    if (a.supported !== b.supported) return a.supported ? -1 : 1;
    if (a.source !== b.source) return a.source === MapSource.Official ? -1 : 1;
    return a.displayName.localeCompare(b.displayName, "en", { sensitivity: "base" });
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
    const maps = await scanMaps(fs, located);

    return {
        ok: masterBundle !== null,
        kind: located.kind,
        rootName: fs.name,
        installPath,
        // The workshop content directory is only inside the grant when a Steam library was picked.
        workshopPath: workshopPath !== null && (await fs.isDirectory(workshopPath)) ? workshopPath : null,
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
export async function scanMaps(fs, located) {
    const maps = [];
    const seen = new Set();

    for (const { path, source } of searchDirectories(located)) {
        for (const candidate of await fs.listDirectories(path)) {
            const entry = await readMap(fs, candidate.path, source);
            if (entry !== null) {
                add(entry);
                continue;
            }
            if (source !== MapSource.Workshop) continue;

            // A workshop item wraps its map in one more folder level, and may hold a mod and no map.
            for (const nested of await fs.listDirectories(candidate.path)) {
                const nestedEntry = await readMap(fs, nested.path, source);
                if (nestedEntry !== null) add(nestedEntry);
            }
        }
    }

    maps.sort(compareForMenu);
    return maps;

    function add(entry) {
        if (seen.has(entry.path)) return;
        seen.add(entry.path);
        // A folder name can appear both as a stale official placeholder and as the real subscribed
        // item; prefer whichever one actually has tiles, as MapCatalog does.
        const existing = maps.findIndex(
            (map) => map.folderName.toLowerCase() === entry.folderName.toLowerCase(),
        );
        if (existing === -1) {
            maps.push(entry);
        } else if (entry.supported && !maps[existing].supported) {
            maps[existing] = entry;
        }
    }
}

function join(...parts) {
    return normalize(parts.filter((part) => part !== null && part !== "").join("/"));
}
