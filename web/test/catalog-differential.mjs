// Differential test: web/lib/catalog.js against core/Data/MapCatalog.cs, over generated install trees.
//
// catalog.js mirrors four types from core/ — UnturnedInstall (where the install and its masterbundle
// are), ContentSource, MapCatalog and LevelInfo — down to copying LevelInfo's TileRegex "exactly,
// including its case sensitivity". Nothing checked that. web/test/run.mjs runs it against a real
// install, which proves it agrees with REALITY on the one tree the dedicated server ships: every map
// there has a Level.dat, an English.dat with a name in it, well-spelled tiles and no workshop items at
// all. The shapes that make the two implementations disagree are the ones a real install does not have.
//
// So this does for the catalogue what differential.mjs does for the .dat reader: it builds trees out of
// the features that actually distinguish them — a missing Level.dat, a name that is whitespace, a tile
// filename spelled four wrong ways, a workshop item wrapping its map one folder deeper, a stale bundled
// copy standing in for a subscribed map — materializes each on disk, and runs BOTH readers over the
// same bytes. Same disk, two readers, so nothing is being modelled twice.
//
// Skips when the .NET SDK is absent, like the rest of the suite skips without content or a browser.

import { execFileSync } from "node:child_process";
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, rmSync } from "node:fs";
import { readdir, readFile, stat as statFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, join, posix } from "node:path";
import { fileURLToPath } from "node:url";
import { scanMaps, locateInstall, findMasterBundle, PickKind } from "../lib/catalog.js";
import { ReadOnlyFs } from "../lib/read-only-fs.js";
import { normalize } from "../lib/paths.js";
import { ordinalIgnoreCaseKey as upper } from "../lib/dotnet.js";

const here = dirname(fileURLToPath(import.meta.url));
const repo = join(here, "..", "..");

// The platform enters the catalogue twice, and the two are not the same question.
//
// Which masterbundle suffix is preferred is a PARAMETER on both sides — UnturnedInstall.FindMasterBundle
// takes one and findMasterBundle takes one — so it is varied per case below, with both told the same
// thing.
//
// Whether a path comparison folds case is not. MapCatalog reads that off OperatingSystem.IsWindows(),
// the machine actually running, while catalog.js takes it from the platform it is handed. Telling the
// browser "windows" on a Linux runner would be asking the two to disagree about the HOST rather than
// about the catalogue. So the scan runs at the host's own platform — which is also what makes the
// Level.dat/level.dat row below mean the same thing on both sides, since they read one real filesystem.
const HOST_PLATFORM =
    process.platform === "win32" ? "windows" : process.platform === "darwin" ? "mac" : "linux";
const BUNDLE_PLATFORMS = ["linux", "windows", "mac"];

// --- The pieces a generated install is assembled from -------------------------------------------------
//
// Each group is one axis the two implementations could disagree along. They are combined rather than
// listed, because it is the combinations that bite: an unsupported official folder is only interesting
// next to a subscribed map with the same folder name.

// What Level.dat's presence decides: whether the folder is a map at all.
const LEVEL = [
    { label: "level", files: { "Level.dat": "Type Survival\n" } },
    { label: "no-level", files: {} },
    // The catalogue's own test is File.Exists on this exact spelling; a case-sensitive host has no map
    // here and a case-insensitive one does. Both sides are told the platform, so both must agree.
    { label: "lower-level", files: { "level.dat": "Type Survival\n" } },
];

// English.dat, which supplies the display name and the blurb. The interesting rows are the ones that
// make MapCatalog.Read fall back on the folder name: absent, empty, and whitespace — and the fallback
// is IsNullOrWhiteSpace, not emptiness, so a name of spaces is not the same question as a missing one.
const LOCALIZATION = [
    { label: "none", files: {} },
    { label: "named", files: { "English.dat": "Name Prince Edward Island\nDescription An island.\n" } },
    { label: "empty-name", files: { "English.dat": 'Name ""\nDescription Blurb\n' } },
    { label: "spaces", files: { "English.dat": 'Name "   "\nDescription Blurb\n' } },
    // U+0085 is whitespace to .NET and not to JavaScript's trim; U+FEFF is the reverse. catalog.js uses
    // its own isNullOrWhiteSpace for exactly this, so both spellings belong in the corpus.
    { label: "nel", files: { "English.dat": 'Name ""\n' } },
    { label: "bom-name", files: { "English.dat": 'Name "﻿"\n' } },
    // A bare key holds null now, so this is the fallback path through a different door.
    { label: "bare-name", files: { "English.dat": "Name\nDescription Blurb\n" } },
    { label: "no-name", files: { "English.dat": "Description Only a blurb.\n" } },
    // A block takes the key away from the scalars entirely.
    { label: "name-block", files: { "English.dat": "Name\n{\nInner X\n}\nDescription Blurb\n" } },
    // The truncation case, at the level the catalogue actually reads.
    { label: "stray-brace", files: { "English.dat": "Name Before\n}\nDescription After\n" } },
    // Read through TextFile.ReadAllText / decodeText: a UTF-16 English.dat is what Windows editors
    // produced by default for years.
    { label: "utf16", files: { "English.dat": { utf16: "Name Utf Sixteen\nDescription Wide.\n" } } },
    { label: "unreadable", files: { "English.dat": { bytes: [0xff, 0xfe, 0x00] } } },
];

// Landscape tiles. LevelInfo's regex is anchored, case-sensitive and demands the _Source suffix, and
// each of those is a way for a map to look playable to one side and not the other.
const TILES = [
    { label: "none", files: {} },
    { label: "one", tiles: ["Tile_0_0_Source.heightmap"] },
    { label: "square", tiles: ["Tile_0_0_Source.heightmap", "Tile_1_1_Source.heightmap"] },
    { label: "negative", tiles: ["Tile_-1_-2_Source.heightmap", "Tile_3_4_Source.heightmap"] },
    // No _Source suffix: a pre-2020 map, which the desktop will not load either.
    { label: "suffixless", tiles: ["Tile_0_0.heightmap"] },
    // Wrong case: rejected by the regex on both sides, however the host filesystem behaves.
    { label: "lowercase", tiles: ["tile_0_0_source.heightmap"] },
    { label: "mixed", tiles: ["Tile_0_0_Source.heightmap", "Tile_x_y_Source.heightmap"] },
    // int.TryParse fails, so the tile is skipped rather than overflowing the span.
    { label: "overflow", tiles: ["Tile_99999999999_0_Source.heightmap", "Tile_0_0_Source.heightmap"] },
    // The 32-bit span both sides compute in. Two separate overflows live in that arithmetic and one
    // case does not reach both: here maxX - minX itself wraps (4294967295 becomes -1)...
    { label: "extremes", tiles: ["Tile_-2147483648_0_Source.heightmap", "Tile_2147483647_0_Source.heightmap"] },
    // ...and here the subtraction fits exactly, so it is the `+ 1` that wraps instead, turning the span
    // negative. Without the second row, dropping the truncation on the span changed nothing.
    { label: "span-overflow", tiles: ["Tile_0_0_Source.heightmap", "Tile_2147483647_0_Source.heightmap"] },
    { label: "not-a-tile", files: { "Landscape/Heightmaps/readme.txt": "hello\n" } },
];

// Config.json, read with comments and trailing commas allowed on the desktop and neither in JSON.parse.
//
// Use_Legacy_Ground shares this axis rather than getting one of its own, because it and Category come
// out of the SAME parse: a document that fails, or that is not an object, loses both together, while a
// Category the desktop cannot decode must NOT cost the map its terrain declaration.
//
// Without the rows that declare Landscape, every generated map defaults to legacy terrain and
// `supported` is false on both sides for all 400 — agreement no port could fail. The differential has
// to see a true before it means anything.
const CONFIG = [
    { label: "none", files: {} },
    { label: "plain", files: { "Config.json": '{"Category":"Official"}' } },
    { label: "comments", files: { "Config.json": '{/* c */"Category":"Curated"//x\n}' } },
    { label: "trailing", files: { "Config.json": '{"Category":"Curated",}' } },
    { label: "comment-then-comma", files: { "Config.json": '{"Category":"Curated"/*c*/,}' } },
    { label: "not-a-string", files: { "Config.json": '{"Category":7}' } },
    { label: "not-an-object", files: { "Config.json": '["Category"]' } },
    { label: "malformed", files: { "Config.json": "{" } },
    { label: "surrogate", files: { "Config.json": '{"Category":"\\ud800"}' } },
    // The declaration itself. False is a Landscape map — the only way `supported` is ever true — and
    // the rest are values LevelConfigData.Bool refuses to coerce: it tests JsonElement.ValueKind, where
    // JavaScript would call the string "false" truthy and the number 0 falsy.
    { label: "landscape", files: { "Config.json": '{"Category":"Official","Use_Legacy_Ground":false}' } },
    { label: "legacy-explicit", files: { "Config.json": '{"Use_Legacy_Ground":true}' } },
    { label: "landscape-string", files: { "Config.json": '{"Use_Legacy_Ground":"false"}' } },
    { label: "landscape-number", files: { "Config.json": '{"Use_Legacy_Ground":0}' } },
    { label: "landscape-null", files: { "Config.json": '{"Use_Legacy_Ground":null}' } },
    // A Landscape declaration next to a category the desktop cannot decode. The surrogate costs the
    // category and nothing else, so a map with tiles is still supported — which is the whole reason the
    // C# catches that exception per string instead of around the parse.
    {
        label: "landscape-surrogate-category",
        files: { "Config.json": '{"Category":"\\ud800","Use_Legacy_Ground":false}' },
    },
    // And the declaration behind the relaxations, so the terrain answer survives the comment and
    // trailing-comma passes rather than only the category doing so.
    { label: "landscape-comments", files: { "Config.json": '{/* c */"Use_Legacy_Ground":false,//x\n}' } },
];

// The artwork the menu shows, which is three independent File.Exists calls.
const ART = [
    { label: "none", files: {} },
    { label: "icon", files: { "Icon.png": "png" } },
    { label: "all", files: { "Icon.png": "png", "Preview.png": "png", "Chart.png": "png" } },
];

// Where the map folder sits. This is the axis that drives MapCatalog.Scan's placeholder logic rather
// than Read's, so the names are deliberately allowed to collide across sources.
const PLACEMENT = [
    { label: "official", root: "official" },
    { label: "bundled", root: "bundled" },
    { label: "workshop-direct", root: "workshop" },
    { label: "workshop-nested", root: "workshop", nested: "MapFolder" },
    { label: "workshop-nested-twice", root: "workshop", nested: "A/B" },
];

// Folder names, including the ones that make the OrdinalIgnoreCase key slot collide. The Greek pair
// upcases alike and lowercases differently, which is the case catalog.js's ordinalIgnoreCaseKey exists
// for, so a subscribed map replaces a stale placeholder on both sides or on neither.
const NAMES = ["PEI", "pei", "Ireland", "Νησος", "Νησοσ", "A B", "dot.name"];

// MapCatalog.Scan's placeholder logic needs several folders with the SAME name across different
// sources, and randomly drawn names produce that far too rarely to rely on: with the names picked
// independently, removing the line that releases the name slot changed nothing in 400 installs. So the
// collisions are scripted. Each entry lists the folders to emit, in the order Scan would meet them.
//
//   * A placeholder is a bundled copy, or an official folder with no loadable terrain. Either can be a
//     stale stand-in for a map the player is really subscribed to.
//   * A subscribed map replaces one — and then RELEASES the name, so a second item with the same folder
//     name is listed on its own instead of being folded into the first.
//   * The Greek pair is the same slot under OrdinalIgnoreCase and two slots under toLowerCase.
// Two folders whose names fold together must not share one ENUMERATED directory, and the scripted
// entries below are built so that they never do. The reason is not fussiness: Directory.
// EnumerateDirectories and node's readdir hand back the same folder in different orders — measured, not
// assumed — and which of two same-named folders Scan meets first is what decides the outcome. That
// order is a property of the filesystem rather than of either implementation, so a corpus that depended
// on it would be reporting the OS, not a divergence. Across the three search directories the order IS
// fixed, and that is the realistic case anyway: a stale bundled copy against a Steam subscription.
//
// The one exception is the pair of workshop items in the third entry, which do share the workshop
// content directory. They are therefore identical in every compared field but their path, so either
// order produces the same comparison — while still failing loudly if the name slot is not released,
// because then one of them is never listed at all.
const COLLISIONS = [
    // Nothing scripted: let the random placements above stand on their own.
    null,
    // Stale bundled copy, then the subscribed map that supersedes it.
    [
        { root: "bundled", name: "Ireland", tiles: "one", display: "Stale" },
        { root: "workshop", nested: "Ireland", tiles: "square", display: "Subscribed" },
    ],
    // ...and a second subscribed item with the same folder name, which is a different map and must be
    // listed as well. This is the case the released name slot exists for.
    [
        { root: "bundled", name: "Ireland", tiles: "one", display: "Stale" },
        { root: "workshop", nested: "Ireland", tiles: "square", display: "Subscribed" },
        { root: "workshop", nested: "Ireland", tiles: "square", display: "Subscribed" },
    ],
    // An unsupported official folder as the placeholder instead.
    [
        { root: "official", name: "Ireland", tiles: "suffixless", display: "Legacy" },
        { root: "workshop", nested: "Ireland", tiles: "one", display: "Subscribed" },
    ],
    // An unsupported official folder replaced by a SUPPORTED bundled one is the other branch: the
    // entry is swapped but the name slot is kept.
    [
        { root: "official", name: "PEI", tiles: "none", display: "Empty" },
        { root: "bundled", name: "PEI", tiles: "one", display: "Copy" },
    ],
    // Case-folded collisions, which are one slot to OrdinalIgnoreCase.
    [
        { root: "bundled", name: "PEI", tiles: "one", display: "Copy" },
        { root: "workshop", nested: "pei", tiles: "square", display: "Subscribed" },
    ],
    // The Greek pair: "Νησος" and "Νησοσ" upcase alike, so the desktop folds them into one slot and a
    // lowercase fold here would leave them as two.
    [
        { root: "bundled", name: "Νησος", tiles: "one", display: "Copy" },
        { root: "workshop", nested: "Νησοσ", tiles: "square", display: "Subscribed" },
    ],
    [
        { root: "official", name: "Νησοσ", tiles: "none", display: "Empty" },
        { root: "workshop", nested: "Νησος", tiles: "one", display: "Subscribed" },
    ],
    // Three sources, one name, met in SearchDirectories order: official, bundled, then workshop.
    [
        { root: "official", name: "Ireland", tiles: "none", display: "Empty" },
        { root: "bundled", name: "Ireland", tiles: "none", display: "Copy" },
        { root: "workshop", nested: "Ireland", tiles: "one", display: "Subscribed" },
    ],
];

// --- The generator ------------------------------------------------------------------------------------

// Deterministic, for the same reason differential.mjs is: a failure should be a bug report rather than a
// story about a seed. Math.imul and the high bits, as there.
function makeRandom(seed) {
    let state = seed >>> 0;
    return (bound) => {
        state = (Math.imul(state, 1103515245) + 12345) >>> 0;
        return (state >>> 16) % bound;
    };
}

function pick(next, table) {
    return table[next(table.length)];
}

// One generated install: a shape (is the picked folder the install, or a Steam library?) and a handful
// of map folders scattered across the three search directories.
function makeCase(next, index) {
    const library = next(2) === 0;
    const installPath = library ? "steamapps/common/Unturned" : "";
    const workshopPath = library ? `steamapps/workshop/content/304930` : null;

    const files = {};
    // Every install has a Bundles folder — that is what makes it one — and usually a masterbundle.
    // Which suffixes EXIST and which platform is asking are drawn independently, so the fallback order
    // is exercised rather than just the native hit.
    const bundlePlatform = pick(next, BUNDLE_PLATFORMS);
    files[posixJoin(installPath, "Bundles/.keep")] = "";
    // A SUBSET of the three variants, not one of them. With a single bundle on disk every preference
    // order finds the same file, so the order itself was untested: reversing the Windows list changed
    // nothing across 400 installs. Only an install carrying two or more variants can tell them apart,
    // which is also the real case the fallback exists for -- a copy of another platform's install.
    const present = ["", "_linux", "_mac"].filter(() => next(2) === 0);
    for (const suffix of present) {
        files[posixJoin(installPath, `Bundles/core${suffix}.masterbundle`)] = "bundle-bytes";
    }

    let item = 1000;
    const emit = (base, groups) => {
        for (const group of groups) {
            for (const [relativePath, content] of Object.entries(group.files ?? {})) {
                files[posixJoin(base, relativePath)] = content;
            }
            for (const tile of group.tiles ?? []) {
                files[posixJoin(base, "Landscape/Heightmaps", tile)] = "heightmap-bytes";
            }
        }
    };
    const baseFor = (root, name, nested) => {
        let path;
        if (root === "official") path = posixJoin(installPath, "Maps", name);
        else if (root === "bundled") path = posixJoin(installPath, "Bundles/Workshop/Maps", name);
        else path = posixJoin(workshopPath, `${item++}`);
        return nested ? posixJoin(path, nested) : path;
    };

    // Every folded folder name used so far. The scripted collisions claim theirs first, and the random
    // maps below then skip any name that would fold onto one already present — so the ONLY same-name
    // folders in a generated install are the ones written down above, whose order is fixed.
    const usedNames = new Set();

    // The scripted collisions first, so the placeholder is met before the map that supersedes it.
    const collision = pick(next, COLLISIONS);
    if (collision !== null) {
        for (const folder of collision) {
            if (folder.root === "workshop" && !library) continue;
            const name = folder.nested ?? folder.name;
            usedNames.add(upper(name));
            emit(baseFor(folder.root, folder.name, folder.nested), [
                LEVEL[0], // a scripted folder is always a map; that is what makes it a placeholder
                { files: { "English.dat": `Name ${folder.display}\nDescription Scripted.\n` } },
                TILES.find((entry) => entry.label === folder.tiles) ?? TILES[0],
            ]);
        }
    }

    const count = 1 + next(4);
    for (let n = 0; n < count; n++) {
        const placement = pick(next, PLACEMENT);
        if (placement.root === "workshop" && !library) continue; // unreachable without the library grant

        // The map's folder name is the LAST segment of where it ends up: the drawn name for an official
        // or bundled folder, the nested folder for a workshop item that wraps its map, and the item id
        // itself when it does not — and an item id is unique, so only the first two can collide.
        const drawn = pick(next, NAMES);
        const folderName = placement.nested
            ? placement.nested.split("/").pop()
            : placement.root === "workshop"
              ? null
              : drawn;
        if (folderName !== null) {
            if (usedNames.has(upper(folderName))) continue;
            usedNames.add(upper(folderName));
        }

        emit(baseFor(placement.root, drawn, placement.nested), [
            pick(next, LEVEL),
            pick(next, LOCALIZATION),
            pick(next, TILES),
            pick(next, CONFIG),
            pick(next, ART),
        ]);
    }

    return { index, library, installPath, workshopPath, bundlePlatform, files };
}

function posixJoin(...parts) {
    return posix.join(...parts.filter((part) => part !== null && part !== ""));
}

// --- Materializing a case on disk ---------------------------------------------------------------------

function writeCase(root, testCase) {
    for (const [relativePath, content] of Object.entries(testCase.files)) {
        const full = join(root, ...relativePath.split("/"));
        mkdirSync(dirname(full), { recursive: true });
        writeFileSync(full, toBytes(content));
    }
}

function toBytes(content) {
    if (typeof content === "string") return Buffer.from(content, "utf8");
    if (content.utf16 !== undefined) {
        // With the byte-order mark, which is what both readers detect the encoding from.
        return Buffer.concat([Buffer.from([0xff, 0xfe]), Buffer.from(content.utf16, "utf16le")]);
    }
    return Buffer.from(content.bytes);
}

// --- A filesystem over a real directory ---------------------------------------------------------------
//
// catalog.js is written against the ReadOnlyFs interface and never learns which backend it got, so the
// differential gives it one that reads the same bytes core/ reads through System.IO. That is the point:
// the alternative — building a ListingFs out of an in-memory listing — would put a second model of the
// tree between the two readers, and a disagreement could then be the listing rather than the catalogue.
class NodeFs extends ReadOnlyFs {
    #root;
    #name;

    constructor(root, name) {
        super();
        this.#root = root;
        this.#name = name;
    }

    get name() {
        return this.#name;
    }

    #resolve(path) {
        const parts = normalize(path).split("/").filter(Boolean);
        return parts.length === 0 ? this.#root : join(this.#root, ...parts);
    }

    async isDirectory(path) {
        try {
            return (await statFile(this.#resolve(path))).isDirectory();
        } catch {
            return false;
        }
    }

    async isFile(path) {
        try {
            return (await statFile(this.#resolve(path))).isFile();
        } catch {
            return false;
        }
    }

    async stat(path) {
        try {
            const info = await statFile(this.#resolve(path));
            return info.isFile() ? { path: normalize(path), size: info.size, lastModified: 0 } : null;
        } catch {
            return null;
        }
    }

    async listDir(path) {
        const base = normalize(path);
        let entries;
        try {
            entries = await readdir(this.#resolve(path), { withFileTypes: true });
        } catch {
            return [];
        }
        return entries.map((entry) => ({
            name: entry.name,
            kind: entry.isDirectory() ? "directory" : "file",
            path: base === "" ? entry.name : `${base}/${entry.name}`,
        }));
    }

    async file(path) {
        const full = this.#resolve(path);
        try {
            const bytes = await readFile(full);
            return {
                size: bytes.length,
                arrayBuffer: async () => bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.length),
                slice: (start, end) => ({
                    arrayBuffer: async () => {
                        const part = bytes.subarray(start, end);
                        return part.buffer.slice(part.byteOffset, part.byteOffset + part.length);
                    },
                }),
            };
        } catch {
            return null;
        }
    }
}

// --- The two sides ------------------------------------------------------------------------------------

async function browserOutcome(root, testCase) {
    const fs = new NodeFs(root, "picked");
    const located = await locateInstall(fs);
    const bundle =
        located.installPath === null
            ? null
            : await findMasterBundle(fs, located.installPath, testCase.bundlePlatform);
    const maps =
        located.kind === PickKind.Unknown ? [] : await scanMaps(fs, located, { platform: HOST_PLATFORM });

    return {
        kind: located.kind,
        installPath: located.installPath,
        bundle: bundle === null ? null : bundle.path,
        maps: maps.map((entry) => ({
            folderName: entry.folderName,
            path: entry.path,
            displayName: entry.displayName,
            source: entry.source,
            description: entry.description ?? null,
            category: entry.category ?? null,
            tileCount: entry.tileCount,
            sizeMetres: entry.sizeMetres,
            supported: entry.supported,
            icon: entry.iconPath !== null,
            preview: entry.previewPath !== null,
            chart: entry.chartPath !== null,
        })),
    };
}

const SHIM = String.raw`using System.Text.Json;
using UnturnedGodot.Assets;
using UnturnedGodot.Data;

// One case per line of input: the directory that was "picked", and where the install sits inside it.
var cases = JsonSerializer.Deserialize<List<Case>>(Console.In.ReadToEnd())!;
var outcomes = new List<Outcome>();

foreach (Case c in cases)
{
    string installRoot = c.InstallPath.Length == 0 ? c.Root : Path.Combine(c.Root, c.InstallPath.Replace('/', Path.DirectorySeparatorChar));
    var platform = Enum.Parse<UnturnedInstall.Platform>(c.BundlePlatform, ignoreCase: true);
    string? bundle = UnturnedInstall.FindMasterBundle(installRoot, platform);

    var maps = new List<MapOut>();
    foreach (MapEntry entry in MapCatalog.Scan(installRoot))
    {
        maps.Add(new MapOut(
            entry.FolderName,
            Rel(c.Root, entry.Path),
            entry.DisplayName,
            entry.Source.ToString(),
            entry.Description,
            entry.Category,
            entry.TileCount,
            // Widened to double before serializing. MapEntry.SizeMetres is a float, and System.Text.Json
            // writes a float as the shortest string that round-trips to THAT float -- "-2.1990233E+12" --
            // which JavaScript then reads as a different number entirely. The cast makes the wire value
            // the float's exact value; the comparison rounds the browser's side to float precision to
            // meet it.
            (double)entry.SizeMetres,
            entry.IsSupported,
            entry.IconPath != null,
            entry.PreviewPath != null,
            entry.ChartPath != null));
    }

    outcomes.Add(new Outcome(bundle == null ? null : Rel(c.Root, bundle), maps));
}

File.WriteAllText(args[0], JsonSerializer.Serialize(outcomes));

// Paths are compared relative to the PICKED folder and with forward slashes, because that is the only
// shape both sides can express: the browser has no handle above the folder it was granted.
static string Rel(string root, string path) =>
    Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

record Case(string Root, string InstallPath, string BundlePlatform);
record MapOut(string FolderName, string Path, string DisplayName, string Source, string? Description,
    string? Category, int TileCount, double SizeMetres, bool Supported, bool Icon, bool Preview, bool Chart);
record Outcome(string? Bundle, List<MapOut> Maps);
`;

function desktopOutcomes(cases, roots, project) {
    writeFileSync(
        join(project, "catalog-differential.csproj"),
        `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="${join(repo, "core", "UnturnedGodot.Core.csproj")}" />
  </ItemGroup>
</Project>
`,
    );
    writeFileSync(join(project, "Program.cs"), SHIM);

    const input = cases.map((testCase, n) => ({
        Root: roots[n],
        InstallPath: testCase.installPath,
        BundlePlatform: testCase.bundlePlatform,
    }));
    // To a file, not stdout: `dotnet run` writes build diagnostics to the same stream, so a restore line
    // carrying a '[' would land in the JSON and read like a harness crash.
    const outcomes = join(project, "outcomes.json");
    execFileSync(
        "dotnet",
        ["run", "--project", join(project, "catalog-differential.csproj"), "--verbosity", "quiet", "--", outcomes],
        { input: JSON.stringify(input), encoding: "utf8", maxBuffer: 64 * 1024 * 1024, cwd: repo },
    );
    return JSON.parse(readFileSync(outcomes, "utf8"));
}

// --- Comparison ---------------------------------------------------------------------------------------

async function run() {
    const next = makeRandom(0x5eed1e);
    const cases = [];
    for (let n = 0; n < 400; n++) cases.push(makeCase(next, n));

    // Outside the repo: Directory.Build.props points every project's output at build/, so a scratch
    // project living there would have its own sources excluded from the compile.
    const scratch = mkdtempSync(join(tmpdir(), "catalog-differential-"));
    try {
        const roots = cases.map((testCase, n) => {
            const root = join(scratch, "trees", `case${n}`);
            mkdirSync(root, { recursive: true });
            writeCase(root, testCase);
            return root;
        });

        const project = join(scratch, "project");
        mkdirSync(project, { recursive: true });
        const expected = desktopOutcomes(cases, roots, project);

        let checked = 0;
        const failures = [];
        for (let n = 0; n < cases.length; n++) {
            const mine = await browserOutcome(roots[n], cases[n]);
            const theirs = expected[n];
            checked++;

            const problems = [];
            if (mine.bundle !== (theirs.Bundle ?? null)) {
                problems.push(`bundle: desktop ${JSON.stringify(theirs.Bundle)}, browser ${JSON.stringify(mine.bundle)}`);
            }
            if (mine.maps.length !== theirs.Maps.length) {
                problems.push(`map count: desktop ${theirs.Maps.length}, browser ${mine.maps.length}`);
            }

            // The menu ORDER is compared as the sequence of sort keys, and the entries themselves are
            // compared after a total ordering by path. The two are separated because ties are real:
            // CompareForMenu returns 0 for two maps with the same display name from the same source,
            // and List<T>.Sort is an introsort — unstable — where Array.prototype.sort has been
            // required to be stable since ES2019. So two tying entries can legitimately come out in
            // either order, and that is not something catalog.js could match without reimplementing
            // .NET's sort. What IS a promise both sides make is the sequence of keys those entries are
            // ordered by, and that is identical however the ties fall.
            const keysOf = (entry) => `${entry.supported ? 0 : 1}|${entry.source}|${upper(entry.displayName)}`;
            const mineKeys = mine.maps.map(keysOf).join("\n");
            const theirKeys = theirs.Maps.map((b) => keysOf({
                supported: b.Supported,
                source: b.Source,
                displayName: b.DisplayName,
            })).join("\n");
            if (mineKeys !== theirKeys) {
                problems.push(`menu order: desktop\n${theirKeys}\n  browser\n${mineKeys}`);
            }

            const byPath = (a, b) => (a.path < b.path ? -1 : a.path > b.path ? 1 : 0);
            const mineSorted = [...mine.maps].sort(byPath);
            const theirSorted = [...theirs.Maps].sort((a, b) => (a.Path < b.Path ? -1 : a.Path > b.Path ? 1 : 0));

            const limit = Math.min(mineSorted.length, theirSorted.length);
            for (let m = 0; m < limit; m++) {
                const a = mineSorted[m];
                const b = theirSorted[m];
                const fields = [
                    ["folderName", a.folderName, b.FolderName],
                    ["path", a.path, b.Path],
                    ["displayName", a.displayName, b.DisplayName],
                    ["source", a.source, b.Source],
                    ["description", a.description, b.Description ?? null],
                    ["category", a.category, b.Category ?? null],
                    ["tileCount", a.tileCount, b.TileCount],
                    // At float precision, because that is what the desktop stores it in: catalog.js
                    // computes in doubles and MapEntry.SizeMetres is a float, so anything past 24 bits
                    // of mantissa is a difference in the TYPE rather than in the measurement.
                    ["sizeMetres", Math.fround(a.sizeMetres), b.SizeMetres],
                    ["supported", a.supported, b.Supported],
                    ["icon", a.icon, b.Icon],
                    ["preview", a.preview, b.Preview],
                    ["chart", a.chart, b.Chart],
                ];
                for (const [field, browser, desktop] of fields) {
                    if (browser !== desktop) {
                        problems.push(
                            `map ${m} [${field}]: desktop ${JSON.stringify(desktop)}, browser ${JSON.stringify(browser)}`,
                        );
                    }
                }
            }

            if (problems.length > 0) {
                failures.push(
                    `case ${n} (${cases[n].library ? "steam library" : "install"})\n` +
                        `  tree: ${Object.keys(cases[n].files).sort().join("\n        ")}\n  ` +
                        problems.join("\n  "),
                );
            }
        }

        if (failures.length > 0) {
            console.error(failures.slice(0, 5).join("\n\n"));
            console.error(`\n${failures.length} of ${checked} generated installs disagree.`);
            // exitCode rather than exit(): process.exit() would skip the finally that removes the tree.
            process.exitCode = 1;
            return;
        }
        console.log(`catalog differential: ${checked} generated installs agree.`);
    } finally {
        rmSync(scratch, { recursive: true, force: true });
    }
}

try {
    execFileSync("dotnet", ["--version"], { stdio: "ignore" });
} catch {
    console.log("catalog differential: skipped, no .NET SDK.");
    process.exit(0);
}
await run();
