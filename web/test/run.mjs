// Runs web/lib against real game content in a real Chromium.
//
//   node web/test/run.mjs                 # uses UNTURNED_PATH, or build/game-data
//   node web/test/run.mjs --keep-open     # leave the browser up to poke at
//
// Self-skips (exit 0) when neither the game content nor Playwright's Chromium is present, so it behaves
// like the C# suite's data-backed tests: green on a bare machine, meaningful on one with the content.

import { createServer } from "node:http";
import { createReadStream, existsSync } from "node:fs";
import { extname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { buildManifest, forProbe } from "./seed.mjs";

const here = fileURLToPath(new URL(".", import.meta.url));
const webRoot = resolve(here, "..");
const repoRoot = resolve(webRoot, "..");
const keepOpen = process.argv.includes("--keep-open");

const MIME = {
    ".html": "text/html; charset=utf-8",
    ".js": "text/javascript; charset=utf-8",
    ".mjs": "text/javascript; charset=utf-8",
    ".css": "text/css; charset=utf-8",
    ".json": "application/json; charset=utf-8",
    ".png": "image/png",
    ".svg": "image/svg+xml",
};

const content = process.env.UNTURNED_PATH || join(repoRoot, "build", "game-data");
if (!existsSync(join(content, "Bundles"))) {
    console.log(
        `SKIP: no game content at ${content} (run ./scripts/fetch-game-data.sh or set UNTURNED_PATH)`,
    );
    process.exit(0);
}

// Playwright is a developer tool, not a dependency of this repo: there is no package.json here and none
// is wanted. A local install is used if there is one, otherwise the global one, and if neither exists the
// run skips.
const chromium = await importPlaywright();
if (!chromium) {
    console.log("SKIP: playwright is not installed (npm install -g playwright)");
    process.exit(0);
}

async function importPlaywright() {
    const candidates = ["playwright"];
    const globalRoot = process.env.NODE_PATH || "/usr/lib/node_modules:/usr/local/lib/node_modules";
    for (const directory of globalRoot.split(":")) {
        if (directory) candidates.push(join(directory, "playwright", "index.js"));
    }
    try {
        const { execSync } = await import("node:child_process");
        candidates.push(join(execSync("npm root -g", { encoding: "utf8" }).trim(), "playwright", "index.js"));
    } catch {
        // No npm on PATH: the direct candidates are all there is to try.
    }

    for (const candidate of candidates) {
        try {
            // Playwright is CommonJS, so importing it by path lands everything under `default`.
            const module = await import(candidate);
            const browsers = module.chromium ? module : module.default;
            if (browsers?.chromium) return browsers.chromium;
        } catch {
            continue;
        }
    }
    return null;
}

const manifest = forProbe(await buildManifest(content));
const inlined = manifest.entries.filter((entry) => entry.data !== null).length;
console.log(
    `Seeding ${manifest.entries.length} paths from ${content} ` +
        `(${inlined} with contents, the rest as empty placeholders).`,
);

const server = createServer((request, response) => {
    const path = decodeURIComponent(new URL(request.url, "http://x").pathname);
    // The harness only ever serves this repo's own web/ folder.
    const file = resolve(webRoot, `.${path === "/" ? "/index.html" : path}`);
    if (!file.startsWith(webRoot) || !existsSync(file)) {
        response.writeHead(404).end("not found");
        return;
    }
    response.writeHead(200, { "content-type": MIME[extname(file)] ?? "application/octet-stream" });
    createReadStream(file).pipe(response);
});
await new Promise((done) => server.listen(0, "127.0.0.1", done));
const origin = `http://127.0.0.1:${server.address().port}`;

let browser;
try {
    browser = await chromium.launch();
} catch (error) {
    console.log(`SKIP: could not launch Chromium (${error.message.split("\n")[0]})`);
    server.close();
    process.exit(0);
}

// One context for both pages: browser.newPage() would give each its own storage partition, and the demo
// has to see the origin-private file system the suite seeded.
const context = await browser.newContext();
const page = await context.newPage();
const pageErrors = [];
page.on("pageerror", (error) => pageErrors.push(String(error)));
page.on("console", (message) => {
    if (message.type() === "error") pageErrors.push(message.text());
});

await page.goto(`${origin}/test/harness.html`);
await page.waitForFunction(() => typeof globalThis.runSuite === "function");
const results = await page.evaluate((payload) => globalThis.runSuite(payload), manifest);

// The demo page itself, driven end to end. The native picker dialog cannot be automated, so
// showDirectoryPicker is stubbed to hand back the OPFS root the suite already seeded — which is the same
// type it would return for real, so everything downstream of the click is the shipping path.
const demoResults = await runDemo();

async function runDemo() {
    const demo = await context.newPage();
    demo.on("pageerror", (error) => pageErrors.push(`demo: ${error}`));
    demo.on("console", (message) => {
        if (message.type() === "error") pageErrors.push(`demo: ${message.text()}`);
    });

    await demo.addInitScript(() => {
        globalThis.showDirectoryPicker = async () =>
            (await navigator.storage.getDirectory()).getDirectoryHandle("install");
    });
    await demo.goto(`${origin}/index.html`);
    await demo.click("#pick");
    await demo.waitForSelector(".card h3");

    const observed = await demo.evaluate(() => ({
        status: document.querySelector("#status").textContent,
        facts: [...document.querySelectorAll("#summary dd")].map((node) => node.textContent),
        cards: [...document.querySelectorAll(".card h3")].map((node) => node.textContent),
        artwork: [...document.querySelectorAll(".card .art img")].length,
    }));

    // build/ is git-ignored, which is where every other generated artifact in this repo goes.
    await demo.screenshot({ path: join(repoRoot, "build", "web-test", "demo.png"), fullPage: true });
    await demo.close();

    return [
        // PEI's English.dat carries only a Description, so the folder name is the display name — the
        // same fallback MapCatalog.Read applies.
        { name: "demo lists PEI", ok: observed.cards.includes("PEI"), detail: observed.cards.join(", ") },
        {
            name: "demo names the master bundle",
            ok: observed.facts.some((fact) => fact.includes("core_linux.masterbundle")),
            detail: observed.facts.join(" | "),
        },
        {
            name: "demo reports the scan",
            ok: /Scanned .* map folder/.test(observed.status),
            detail: observed.status,
        },
        { name: "demo renders map artwork", ok: observed.artwork > 0, detail: `${observed.artwork} images` },
    ];
}

let passed = 0;
let failed = 0;
results.push(...demoResults);
for (const result of results) {
    if (result.ok) {
        passed++;
        console.log(`  ok   ${result.name}`);
    } else {
        failed++;
        console.log(`  FAIL ${result.name}${result.detail ? `: ${result.detail}` : ""}`);
    }
}

for (const error of pageErrors) {
    failed++;
    console.log(`  FAIL page error: ${error}`);
}

console.log(`\n${passed} passed, ${failed} failed`);

if (keepOpen) {
    console.log(`Browser left open at ${origin}. Ctrl-C to stop.`);
} else {
    await browser.close();
    server.close();
    process.exit(failed === 0 ? 0 : 1);
}
