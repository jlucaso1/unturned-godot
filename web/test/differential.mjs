// Differential test: web/lib/dat.js against core/Dat/DatParser.cs, over generated documents.
//
// The hand-written reader this replaced was wrong about the grammar in a new way in each of three
// consecutive reviews, and every time the fix was checked against the cases someone had thought to write
// down. This checks the cases nobody thought of: it builds documents out of the tokens that actually
// distinguish the two grammars -- brackets in every position, mismatched closers, quotes that cross
// newlines, commas tight and detached, comments -- runs the C# parser over them, and compares the
// top-level Name and Description with what the browser module returns.
//
// Skips when the .NET SDK is absent, like the rest of the suite skips without content or a browser.

import { execFileSync } from "node:child_process";
import { mkdtempSync, readFileSync, writeFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { parseDatTopLevel } from "../lib/dat.js";

const here = dirname(fileURLToPath(import.meta.url));
const repo = join(here, "..", "..");

// The pieces a document is assembled from. Weighted by hand rather than uniformly: brackets and quotes
// are where the grammars can differ, plain pairs are only there to give them something to disagree about.
const PIECES = [
    "Name Plain",
    "Name Value With Spaces",
    'Name "Quoted"',
    'Name "Multi\nLine"',
    'Name "Unterminated',
    "Description Blurb",
    'Description "Quoted Blurb"',
    'Name "A" Description "B"',
    'Name "A", Description "B"',
    '"Name" "Quoted Key"',
    '"Name", "Tight Comma"',
    '"Name" , "Detached Comma"',
    "Name Trailing\\nEscape",
    "{",
    "}",
    "[",
    "]",
    "{ Name Inline",
    '{ Name "Inline" }',
    '[ "item" ]',
    "[ item ]",
    "Nested {",
    "Nested [",
    "Name{",
    "/ a comment",
    "Name / not a comment",
    'Name "Map" / a comment',
    ",Name LeadingComma",
    "",
    "   ",

    // Below: one piece per divergence this harness could not see before, because both sides shared the
    // defect. A generated corpus only rules out what it can generate, and none of these shapes was
    // reachable from the pieces above — so each is written out rather than hoped for.

    // An unbalanced '}' mid-document. The root body has no closer to find, so everything after it is
    // still read; returning there truncated the document.
    "Name Before\n}\nDescription After",
    "Sub\n{\nX 1\n}\n}\nName After",

    // A blank line between key and bracket. At most ONE line break may separate them, so this leaves
    // Name a scalar and the block unintroduced.
    "Name Kept\n\n{\nDescription Buried\n}",
    "Name\n\n[\nitem\n]",
    "Name Kept\n{\nDescription Inline\n}",

    // Unrecognized escapes keep their backslash — the workshop file paths 3.23.7.0 broke.
    "Name Some\\Path",
    "Name C:\\Users\\Maps\\Icon.png",
    'Name "C:\\Users\\Maps"',
    "Name Trailing\\",
    'Name "Trailing\\',
    'Name "Escaped\\"Quote"',
    "Name Escaped\\\"Quote",

    // Mismatched closers, which decide whether the words after them are keys or list values.
    "Metadata\n[\n}\nName Fake\n]",
    "Metadata\n{\n]\nName Real\n}",
    "Metadata\n[\n[\n}\nName Fake\n]\n]",

    // A key with no value at all: DatValue(null), not an empty string.
    "Name",
    "Name\nDescription Blurb",

    // Whitespace that SkipSpacesAndTabs does not skip, so no value starts on it.
    "Name\u000bPlain",
    "Name\u00a0Plain",
];

// A deterministic generator: the same documents on every run and every machine, so a failure is a bug
// report rather than a story about a seed.
//
// Math.imul rather than `*`, because the plain multiply is where this quietly stopped being an LCG:
// `state * 1103515245` exceeds 2^53 for most of the state space, so the low bits round away and the
// sequence degenerates. It still looked random enough to pass, while leaving three of the thirty pieces
// unreachable — the corpus was smaller than it claimed to be, which is exactly the failure a generated
// corpus is supposed to rule out. Taking the *high* bits for the output is the other half: the low bits
// of an LCG have short periods, so `state % 30` is much weaker than a shift down first.
function* documents(count) {
    let state = 0x2f6e2b1;
    const next = (bound) => {
        state = (Math.imul(state, 1103515245) + 12345) >>> 0;
        return (state >>> 16) % bound;
    };
    for (let n = 0; n < count; n++) {
        const lines = [];
        const length = 1 + next(6);
        for (let k = 0; k < length; k++) lines.push(PIECES[next(PIECES.length)]);
        yield lines.join("\n");
    }
}

function run() {
    const cases = [...documents(4000)];
    // Outside the repo on purpose: Directory.Build.props points every project's output at build/, so a
    // scratch project living there would have its own sources excluded from the compile.
    const project = mkdtempSync(join(tmpdir(), "dat-differential-"));
    try {
        compare(cases, project);
    } finally {
        // In finally, not on the success path: a build failure or a bad parse would otherwise leave the
        // scratch project behind in the system temp directory on every run.
        rmSync(project, { recursive: true, force: true });
    }
}

function compare(cases, project) {
    writeFileSync(
        join(project, "dat-differential.csproj"),
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
    writeFileSync(
        join(project, "Program.cs"),
        `using System.Text.Json;
using UnturnedGodot.Dat;

string[] cases = JsonSerializer.Deserialize<string[]>(Console.In.ReadToEnd())!;
var outcomes = new List<Dictionary<string, string?>>();
foreach (string text in cases)
{
    DatDictionary parsed = DatParser.Parse(text);
    outcomes.Add(new Dictionary<string, string?>
    {
        ["Name"] = parsed.GetString("Name"),
        ["Description"] = parsed.GetString("Description"),
        // Presence as well as content, because the two are no longer the same question: a key written
        // with no value holds null, and so reads back exactly like a key that is not there. Without
        // this the harness cannot see a document losing its keys — which is what an early return on a
        // stray '}' does, and what it could not see before.
        //
        // TryGetString rather than ContainsKey, because that is the question the browser can answer:
        // its DatValues keeps the root's SCALARS and drops a key that turns out to hold a block, so
        // "present and a value" is the shared vocabulary. Whether a block is a list or a dictionary is
        // not something the browser models, and the harness does not invent an answer for it.
        ["HasName"] = parsed.TryGetString("Name", out _) ? "yes" : "no",
        ["HasDescription"] = parsed.TryGetString("Description", out _) ? "yes" : "no",
    });
}
File.WriteAllText(args[0], JsonSerializer.Serialize(outcomes));
`,
    );

    // The outcomes go to a file, not stdout. `dotnet run` writes build diagnostics to the same stream,
    // so any restore line or warning carrying a '[' would land in the JSON and surface as a parse error
    // that reads like a harness crash rather than the build message it is.
    const outcomes = join(project, "outcomes.json");
    execFileSync(
        "dotnet",
        [
            "run",
            "--project",
            join(project, "dat-differential.csproj"),
            "--verbosity",
            "quiet",
            "--",
            outcomes,
        ],
        { input: JSON.stringify(cases), encoding: "utf8", maxBuffer: 64 * 1024 * 1024, cwd: repo },
    );
    const expected = JSON.parse(readFileSync(outcomes, "utf8"));

    let checked = 0;
    const failures = [];
    for (let n = 0; n < cases.length; n++) {
        const values = parseDatTopLevel(cases[n]);
        const mine = {
            Name: values.get("Name") ?? null,
            Description: values.get("Description") ?? null,
            HasName: values.has("Name") ? "yes" : "no",
            HasDescription: values.has("Description") ? "yes" : "no",
        };
        for (const key of ["Name", "Description", "HasName", "HasDescription"]) {
            checked++;
            if (mine[key] !== expected[n][key]) {
                failures.push(
                    `case ${n} [${key}]\n  input:    ${JSON.stringify(cases[n])}\n` +
                        `  desktop:  ${JSON.stringify(expected[n][key])}\n` +
                        `  browser:  ${JSON.stringify(mine[key])}`,
                );
            }
        }
    }

    if (failures.length > 0) {
        console.error(failures.slice(0, 10).join("\n\n"));
        console.error(`\n${failures.length} of ${checked} comparisons disagree.`);
        // exitCode rather than exit(): process.exit() tears the process down without unwinding, so
        // the finally that removes the scratch project would not run on the one path it exists for.
        process.exitCode = 1;
        return;
    }
    console.log(`dat differential: ${checked} comparisons over ${cases.length} documents agree.`);
}

try {
    execFileSync("dotnet", ["--version"], { stdio: "ignore" });
} catch {
    console.log("dat differential: skipped, no .NET SDK.");
    process.exit(0);
}
run();
