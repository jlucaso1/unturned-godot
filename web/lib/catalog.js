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
import { compareOrdinalIgnoreCase, isNullOrWhiteSpace, ordinalIgnoreCaseKey } from "./dotnet.js";

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
    // ONE read of Config.json for the whole entry, because MapCatalog.Read takes one LevelConfigData:
    // the category and the terrain declaration come out of the same document, and reading it twice
    // would let a file that parses differently on the second pass make them disagree.
    const config = await readConfig(fs, mapPath);

    return {
        folderName: folder,
        path: normalize(mapPath),
        // MapCatalog.Read falls back on IsNullOrWhiteSpace, not on emptiness: a localized name of spaces
        // would otherwise leave the card with a blank heading. Not JavaScript's trim() either — the two
        // disagree in both directions (U+0085 is whitespace to .NET and not to trim; U+FEFF the reverse),
        // so a name made of one of those would fall back on one side and not the other.
        displayName: isNullOrWhiteSpace(localization.name) ? folder : localization.name,
        source,
        description: localization.description,
        category: config.category,
        tileCount: count,
        sizeMetres,
        usesLegacyGround: config.usesLegacyGround,
        // Pre-2020 maps store terrain as one legacy Unity Terrain instead of Landscape tiles; this port
        // does not read those, so they are listed and marked rather than hidden.
        //
        // MapEntry.IsSupported: `!UsesLegacyGround && TileCount > 0`. The map's OWN declaration decides
        // which terrain system it is authored for — LevelGround.load returns early into loadTrees() on
        // Use_Legacy_Ground — and the tiles have to be on disk for the loader that reads them to have
        // anything to read. This used to be `count > 0` alone, which made the browser call a map with
        // tiles and no config supported while the desktop, reading the config, called it legacy.
        supported: !config.usesLegacyGround && count > 0,
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

// LevelConfigData's own constructor defaults, which is what the game reads for a map with no config at
// all — and what LevelConfigData.Default hands back for one it cannot parse. Use_Legacy_Ground defaults
// to TRUE, so a map that ships tiles but never declares itself is legacy terrain on both sides. That is
// the game's answer too: Unturned deserializes onto a fresh LevelInfoConfigData whose Use_Legacy_Ground
// the constructor sets true.
const CONFIG_DEFAULTS = Object.freeze({ category: null, usesLegacyGround: true });

// LevelConfigData.Load + Parse, for the two fields the catalogue reads. A missing, unreadable or
// malformed file takes the defaults above, exactly as the C# does — including a root that parses but is
// not an object, which JsonDocument accepts and LevelConfigData then rejects.
async function readConfig(fs, mapPath) {
    const text = await safe(fs.readText(join(mapPath, "Config.json")), null);
    if (text === null) return CONFIG_DEFAULTS;

    let root;
    try {
        root = JSON.parse(relaxJson(text));
    } catch {
        // A map whose config will not parse still loads, on the terrain system the defaults name.
        return CONFIG_DEFAULTS;
    }
    // `root.ValueKind != JsonValueKind.Object`. Arrays, strings and numbers are all valid JSON and none
    // of them is a config; null is typeof "object" in JavaScript and has to be excluded by hand.
    if (root === null || typeof root !== "object" || Array.isArray(root)) return CONFIG_DEFAULTS;

    return { category: readCategory(root), usesLegacyGround: readBool(root, "Use_Legacy_Ground", true) };
}

// LevelConfigData.Bool: only the JSON literals `true` and `false` answer. A number, a string — even
// "true" — or a null leaves the caller's fallback standing, because JsonElement.ValueKind is checked
// rather than the value being coerced. JavaScript would happily call all of those truthy or falsy.
function readBool(root, key, fallback) {
    const value = root[key];
    if (value === true) return true;
    if (value === false) return false;
    return fallback;
}

// LevelConfigData.String -> Text: not a string is null, and a string that cannot be decoded is null too.
//
// The catch in the C# is per STRING rather than around the whole parse, and that split has to survive
// the port: a config whose Category holds an unpaired surrogate still has a perfectly good
// Use_Legacy_Ground, and failing the document wholesale would quietly demote a Landscape map to legacy
// terrain over a bad character in an unrelated field. Reading the two fields independently here is what
// reproduces that — `readBool` above never sees the category's problem.
function readCategory(root) {
    const value = root.Category;
    if (typeof value !== "string") return null;
    // JsonElement.GetString() throws on a value holding an unpaired surrogate — `\uD800` with no low
    // half — where JSON.parse hands it back happily. The desktop has no category for such a config.
    return hasUnpairedSurrogate(value) ? null : value;
}

// A string that cannot be encoded as UTF-8, because a surrogate is missing its partner.
function hasUnpairedSurrogate(text) {
    for (let i = 0; i < text.length; i++) {
        const code = text.charCodeAt(i);
        if (code >= 0xdc00 && code <= 0xdfff) return true; // a low half with nothing before it
        if (code < 0xd800 || code > 0xdbff) continue;
        const low = text.charCodeAt(i + 1);
        if (!(low >= 0xdc00 && low <= 0xdfff)) return true;
        i++; // a complete pair
    }
    return false;
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
    // MeasureLandscape does this in 32-bit integers, so a span between coordinates at opposite ends of
    // the range wraps rather than growing. `| 0` reproduces that: without it a map with tiles at both
    // int extremes would be described here as trillions of metres across and as 1024 on the desktop.
    const spanX = (maxX - minX) | 0;
    const spanY = (maxY - minY) | 0;
    const span = (Math.max(spanX, spanY) + 1) | 0;
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
    //
    // Folded with the same invariant upcase as the name slot below, because both model the same
    // OrdinalIgnoreCase. Only the install path and `Bundles/Workshop/Maps` fall inside this prefix and
    // those are ASCII, so the two foldings agree today — but leaving one function comparing the one .NET
    // rule two different ways is how they stop agreeing later.
    const foldPathCase = isCaseInsensitiveFilesystem(platform);
    const bundledPrefix = foldPathCase
        ? ordinalIgnoreCaseKey(`${bundledWorkshopRoot}/`)
        : `${bundledWorkshopRoot}/`;

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
        const bundledCopy = (foldPathCase ? ordinalIgnoreCaseKey(entry.path) : entry.path).startsWith(
            bundledPrefix,
        );
        const placeholder = bundledCopy || (entry.source === MapSource.Official && !entry.supported);
        // preferredByName is a Dictionary keyed with StringComparer.OrdinalIgnoreCase, which folds by
        // an invariant upcase — not by JavaScript's lowercase. The two disagree wherever a lowercase
        // pair does not round-trip, Greek final sigma most visibly: "Νησος" and "Νησοσ" upcase alike and
        // are one key on the desktop, so a subscribed map replaces the stale placeholder there, and
        // would have been two keys here, leaving both in the menu.
        const key = ordinalIgnoreCaseKey(entry.folderName);
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
