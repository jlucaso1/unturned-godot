// Differential test: web/lib/dotnet.js against the BCL, over every code point.
//
// `ordinalIgnoreCaseKey` and `isWhiteSpace` are tables of exceptions to JavaScript's built-ins, and a
// table of exceptions is the kind of thing that rots quietly — either runtime's Unicode version can move
// under it. Two of the entries were found by review rather than by a test, which is the sign it needed
// one.
//
// The in-browser suite cannot be that test. Chromium's ICU is behind: it does not fold U+16EBB or
// U+10D70 at all, so an assertion that the exclusion table stops them from folding passes whether or not
// the table exists. Node's ICU does know them, so the comparison means something here.
//
// This sweeps the whole of Unicode rather than the interesting-looking parts, because "which code points
// are interesting" is exactly what was got wrong.
//
// Skips when the .NET SDK is absent, like the rest of the suite skips without content or a browser.

import { execFileSync } from "node:child_process";
import { mkdtempSync, readFileSync, writeFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { isWhiteSpace, ordinalIgnoreCaseKey } from "../lib/dotnet.js";

const here = dirname(fileURLToPath(import.meta.url));
const repo = join(here, "..", "..");

const PROGRAM = `using System.Text.Json;

var folds = new List<int>();
var spaces = new List<int>();
for (int c = 0; c <= 0x10FFFF; c++)
{
    if (c >= 0xD800 && c <= 0xDFFF) { folds.Add(c); continue; }
    string s = char.ConvertFromUtf32(c);
    string up = s.ToUpperInvariant();
    // The fold OrdinalIgnoreCase performs: ToUpperInvariant when the two are ordinal-equal, else itself.
    // They are not the same function -- U+017F is upcased by one and not folded by the other.
    bool folded = up != s && string.Equals(s, up, StringComparison.OrdinalIgnoreCase);
    folds.Add(folded ? char.ConvertToUtf32(up, 0) : c);
    if (c <= 0xFFFF && char.IsWhiteSpace((char)c)) spaces.Add(c);
}
File.WriteAllText(args[0], JsonSerializer.Serialize(new { folds, spaces }));
`;

const PROJECT = `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
`;

function run() {
    // Outside the repo: Directory.Build.props points every project's output at build/, so a scratch
    // project living there would have its own sources excluded from the compile.
    const project = mkdtempSync(join(tmpdir(), "dotnet-casing-"));
    try {
        compare(project);
    } finally {
        rmSync(project, { recursive: true, force: true });
    }
}

function compare(project) {
    writeFileSync(join(project, "dotnet-casing.csproj"), PROJECT);
    writeFileSync(join(project, "Program.cs"), PROGRAM);

    const table = join(project, "casing.json");
    execFileSync(
        "dotnet",
        ["run", "--project", join(project, "dotnet-casing.csproj"), "--verbosity", "quiet", "--", table],
        { encoding: "utf8", maxBuffer: 128 * 1024 * 1024, cwd: repo },
    );
    const { folds, spaces } = JSON.parse(readFileSync(table, "utf8"));

    const failures = [];
    let checked = 0;
    for (let c = 0; c <= 0x10ffff; c++) {
        if (c >= 0xd800 && c <= 0xdfff) continue;
        checked++;
        const expected = String.fromCodePoint(folds[c]);
        const actual = ordinalIgnoreCaseKey(String.fromCodePoint(c));
        if (actual !== expected)
            failures.push(`fold U+${hex(c)}: .NET ${show(expected)}, ours ${show(actual)}`);
    }

    const expectedSpaces = new Set(spaces);
    for (let c = 0; c <= 0xffff; c++) {
        if (c >= 0xd800 && c <= 0xdfff) continue;
        checked++;
        const mine = isWhiteSpace(String.fromCharCode(c));
        if (mine !== expectedSpaces.has(c)) {
            failures.push(`whitespace U+${hex(c)}: .NET ${expectedSpaces.has(c)}, ours ${mine}`);
        }
    }

    if (failures.length > 0) {
        console.error(failures.slice(0, 20).join("\n"));
        console.error(`\n${failures.length} of ${checked} code points disagree.`);
        console.error("Regenerate the tables in web/lib/dotnet.js -- a Unicode version has moved.");
        // exitCode rather than exit(): process.exit() tears the process down without unwinding, so
        // the finally that removes the scratch project would not run on the one path it exists for.
        process.exitCode = 1;
        return;
    }
    console.log(`dotnet casing: ${checked} code points agree.`);
}

function hex(c) {
    return c.toString(16).toUpperCase().padStart(4, "0");
}

function show(text) {
    return [...text].map((c) => `U+${hex(c.codePointAt(0))}`).join(" ");
}

try {
    execFileSync("dotnet", ["--version"], { stdio: "ignore" });
} catch {
    console.log("dotnet casing: skipped, no .NET SDK.");
    process.exit(0);
}
run();
