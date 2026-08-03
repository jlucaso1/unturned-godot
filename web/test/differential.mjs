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
        for (const key of ["Name", "Description"]) {
            checked++;
            const mine = values.get(key) ?? null;
            if (mine !== expected[n][key]) {
                failures.push(
                    `case ${n} [${key}]\n  input:    ${JSON.stringify(cases[n])}\n` +
                        `  desktop:  ${JSON.stringify(expected[n][key])}\n` +
                        `  browser:  ${JSON.stringify(mine)}`,
                );
            }
        }
    }

    if (failures.length > 0) {
        console.error(failures.slice(0, 10).join("\n\n"));
        console.error(`\n${failures.length} of ${checked} comparisons disagree.`);
        process.exit(1);
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
