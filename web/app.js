// The demo: pick an Unturned folder, and see what a web build would find in it.
//
// Nothing here loads a map — that is the engine's job and the engine cannot get here yet (see
// docs/WEB-EXPORT.md). What this proves is the half that does not depend on the engine: a browser can be
// pointed at a real install, keep the pick across reloads, and read the same files core/ reads, without
// the content ever leaving the player's disk or being packaged into the build.

import { probeInstall, PickKind, currentPlatform } from "./lib/catalog.js";
import { HandleFs, ensureReadPermission, pickDirectory, supportsDirectoryPicker } from "./lib/handle-fs.js";
import { ListingFs, supportsDirectoryInput } from "./lib/listing-fs.js";
import { forgetHandle, loadHandle, saveHandle } from "./lib/handle-store.js";

const ui = {
    pick: document.querySelector("#pick"),
    reconnect: document.querySelector("#reconnect"),
    forget: document.querySelector("#forget"),
    fallback: document.querySelector("#fallback"),
    fallbackInput: document.querySelector("#fallback-input"),
    support: document.querySelector("#support"),
    status: document.querySelector("#status"),
    summary: document.querySelector("#summary"),
    maps: document.querySelector("#maps"),
};

// Object URLs for map artwork, revoked whenever the listing is replaced.
let artwork = [];
// Cards waiting to come near the viewport before their artwork is read, and the observer watching them.
let artObserver = null;
const artQueue = new Map();

// Work overlaps: restoring a saved folder starts on load, the player can pick a different one while it
// is still running, and both paths write the same status line and listing. Without a generation whichever
// finishes last wins — so a slow restored Steam-library scan could paint over the install the player just
// chose, and its artwork would be read out of the wrong folder. Every path that will touch the UI claims
// a token and checks it after each await; only the newest one is allowed to write.
//
// Declared above start(), which runs on module evaluation and reaches claim() through restoreSaved().
let generation = 0;

function claim() {
    return ++generation;
}

function isCurrent(token) {
    return token === generation;
}

// Lets the browser paint before the caller starts a long synchronous stretch.
function nextFrame() {
    return new Promise((resolve) => requestAnimationFrame(() => resolve()));
}

start();

function start() {
    ui.support.textContent = supportsDirectoryPicker()
        ? `Directory picker available (${currentPlatform()}). Your pick is remembered between reloads.`
        : supportsDirectoryInput()
          ? "This browser has no directory picker, so the folder-upload fallback is used: it re-reads the " +
            "whole listing every time and cannot remember the pick. Chromium-based browsers do better."
          : "This browser can select neither a directory handle nor a directory listing.";

    ui.pick.disabled = !supportsDirectoryPicker();
    ui.fallback.hidden = supportsDirectoryPicker() || !supportsDirectoryInput();

    ui.pick.addEventListener("click", onPick);
    ui.reconnect.addEventListener("click", onReconnect);
    ui.forget.addEventListener("click", onForget);
    ui.fallbackInput.addEventListener("change", onFallbackPick);

    if (supportsDirectoryPicker()) void restoreSaved();
}

// On load, a previously picked folder may come back with its permission still granted (same session, or
// a persisted grant), in which case it is used straight away; otherwise the player gets one button.
async function restoreSaved() {
    // Claimed before the first await: restoring runs unprompted on load, so anything the player does
    // afterwards is newer intent and this continuation must not write over it.
    const token = claim();
    const handle = await loadHandle().catch(() => null);
    if (handle === null || !isCurrent(token)) return;

    const granted = await ensureReadPermission(handle, { prompt: false });
    if (!isCurrent(token)) return;

    ui.forget.hidden = false;
    if (granted) {
        await probe(new HandleFs(handle), null, token);
    } else {
        ui.reconnect.hidden = false;
        setStatus(`Previously picked "${handle.name}". Reconnect to read it again.`);
    }
}

async function onPick() {
    // Claimed after the dialog, not before: the picker can sit open for a long time, and a restore that
    // lands while it is open is still worth showing if the player then cancels.
    const handle = await pickDirectory({ id: "unturned-install" });
    if (handle === null) return;
    const token = claim();

    // Remembering the pick is a convenience; being able to read the folder the player just granted is
    // the point. A browser with storage blocked (private windows, enterprise policy) rejects the write,
    // and that must cost the reconnect button, not the scan.
    let remembered = true;
    try {
        await saveHandle(handle);
    } catch {
        remembered = false;
    }
    // A failed overwrite leaves whatever was stored before intact, and that older folder is what a
    // reload will restore — so Forget still has something to forget, and hiding it would strand it.
    const stored = remembered || (await loadHandle().catch(() => null)) !== null;
    if (!isCurrent(token)) return;
    ui.forget.hidden = !stored;
    ui.reconnect.hidden = true;
    await probe(
        new HandleFs(handle),
        remembered
            ? null
            : stored
              ? "This browser would not save the pick; a reload will restore the folder saved before it."
              : "This browser will not remember the pick.",
        token,
    );
}

async function onReconnect() {
    // Claimed at entry, not after the awaits: requestPermission can leave its prompt open for as long as
    // the player ignores it, and the page stays interactive underneath. Claiming afterwards would hand
    // this the *newest* token once the prompt finally resolves, inverting recency and letting a stale
    // reconnect paint over whatever the player did in the meantime.
    const token = claim();
    const handle = await loadHandle().catch(() => null);
    const granted = handle !== null && (await ensureReadPermission(handle, { prompt: true }));
    if (!isCurrent(token)) return;
    if (!granted) {
        setStatus("Permission denied. Pick the folder again.");
        return;
    }
    ui.reconnect.hidden = true;
    await probe(new HandleFs(handle), null, token);
}

async function onForget() {
    // Retire any scan still in flight, so it cannot repaint the listing this is about to clear — and
    // keep the token, because the delete is itself an await the player can outrun by picking a folder.
    const previous = generation;
    const token = claim();
    try {
        await forgetHandle();
    } catch (error) {
        if (!isCurrent(token)) return;
        // Nothing was cleared, so put the generation back. The cards are still on screen and every
        // lazy artwork task queued against them carries the old token — leaving them retired would
        // mean scrolling the listing produced permanently blank artwork while the status says the
        // folder is still stored and the maps are still usable. Restoring is exact rather than
        // approximate: isCurrent(token) just proved nothing else claimed in between.
        generation = previous;
        // "Forget" is the privacy-facing control on this page: reporting success while the handle is
        // still in IndexedDB, and still restorable on the next load, is the one lie it must not tell.
        setStatus(`Could not forget the saved folder: ${error?.message ?? error}. It is still stored.`);
        return;
    }
    if (!isCurrent(token)) return;
    ui.forget.hidden = true;
    ui.reconnect.hidden = true;
    ui.summary.hidden = true;
    clearMaps();
    setStatus("Forgot the saved folder.");
}

async function onFallbackPick(event) {
    const input = event.target;
    const files = [...input.files];
    // A file input does not fire `change` when the same folder is chosen again, so picking it twice —
    // to retry after a read failure, or to see files that have since changed — would do nothing at all.
    // The FileList is snapshotted above because clearing the value empties it.
    input.value = "";
    if (files.length === 0) return;
    setStatus(`Reading a listing of ${files.length.toLocaleString()} files...`);
    // Indexing the listing is synchronous and, for a Steam library, can run for seconds. Without a
    // frame to paint in, that status never appears and probe() replaces it with "Scanning..." before
    // the browser gets a turn — leaving the page looking frozen under a stale message.
    await nextFrame();
    await probe(new ListingFs(files));
}

async function probe(fs, note = null, token = null) {
    const scan = token ?? claim();
    if (!isCurrent(scan)) return;
    setStatus("Scanning...");
    const started = performance.now();
    let result;
    try {
        result = await probeInstall(fs);
    } catch (error) {
        if (!isCurrent(scan)) return;
        // The listing on screen belongs to the folder that was scanned before this one. Leaving it under
        // an error about the new folder reads as "here is what we found in it", which is the opposite of
        // what happened.
        ui.summary.hidden = true;
        clearMaps();
        setStatus(`Could not read that folder: ${error?.message ?? error}`);
        return;
    }
    if (!isCurrent(scan)) return;
    render(fs, result, performance.now() - started, note, scan);
}

function render(fs, result, elapsedMs, note, scan) {
    clearMaps();

    if (result.kind === PickKind.Unknown) {
        ui.summary.hidden = true;
        setStatus(result.reason);
        return;
    }

    const playable = result.maps.filter((map) => map.supported).length;
    setStatus(
        `Scanned "${result.rootName}" in ${elapsedMs.toFixed(0)} ms — ` +
            `${result.maps.length} map folder${result.maps.length === 1 ? "" : "s"}, ${playable} with Landscape tiles.` +
            (note === null ? "" : ` ${note}`),
    );

    ui.summary.hidden = false;
    ui.summary.innerHTML = "";
    addFact("Picked", result.kind === PickKind.SteamLibrary ? "Steam library" : "Unturned install");
    addFact("Install path", result.installPath === "" ? "(the picked folder)" : result.installPath);
    addFact(
        "Master bundle",
        result.masterBundle === null
            ? "not found — the install looks incomplete"
            : `${result.masterBundle.path.split("/").pop()} (${formatBytes(result.masterBundle.size)}, never uploaded)`,
    );
    addFact(
        "Workshop maps",
        result.workshopPath !== null
            ? `readable at ${result.workshopPath}`
            : result.kind === PickKind.SteamLibrary
              ? "no workshop content downloaded in this library"
              : "outside the granted folder — pick the Steam library instead to include them",
    );

    for (const map of result.maps) ui.maps.append(mapCard(fs, map, scan));
}

function mapCard(fs, map, token) {
    const card = document.createElement("article");
    card.className = "card" + (map.supported ? "" : " unsupported");

    const image = document.createElement("div");
    image.className = "art";
    card.append(image);
    observeArtwork(fs, image, map, token);

    const body = document.createElement("div");
    body.className = "body";
    card.append(body);

    const title = document.createElement("h3");
    title.textContent = map.displayName;
    body.append(title);

    const tags = document.createElement("p");
    tags.className = "tags";
    tags.textContent = [
        map.source,
        map.category && map.category !== map.source ? map.category : null,
        map.supported
            ? `${map.tileCount} tiles · ${map.sizeMetres / 1000} × ${map.sizeMetres / 1000} km`
            : "legacy terrain — cannot load",
    ]
        .filter(Boolean)
        .join(" · ");
    body.append(tags);

    if (map.description) {
        const blurb = document.createElement("p");
        blurb.className = "blurb";
        blurb.textContent = map.description;
        body.append(blurb);
    }

    return card;
}

// Artwork loads when its card comes near the viewport, not when the card is created. A Steam library
// can hold hundreds of maps, and decoding every full-size preview at once is enough to stall the page —
// `<img loading="lazy">` used to give this for free, and stopped applying once the decode moved ahead
// of attaching the element (which is what proves a fallback candidate is really an image).
function observeArtwork(fs, container, map, token) {
    if (typeof IntersectionObserver !== "function") {
        void showArtwork(fs, container, map, token);
        return;
    }
    if (artObserver === null) {
        artObserver = new IntersectionObserver(
            (entries, observer) => {
                for (const entry of entries) {
                    if (!entry.isIntersecting) continue;
                    observer.unobserve(entry.target);
                    const pending = artQueue.get(entry.target);
                    artQueue.delete(entry.target);
                    if (pending !== undefined) void pending();
                }
            },
            // Start a little before the card is actually on screen, so scrolling does not show gaps.
            { rootMargin: "600px" },
        );
    }
    artQueue.set(container, () => showArtwork(fs, container, map, token));
    artObserver.observe(container);
}

// The map's own artwork, read straight off the player's disk as a blob: URL. The folder is passed in
// rather than read from module state, so a card always loads its image out of the folder it came from
// even if another scan has started since — and the scan's token is checked after every await, so a load
// that outlives its listing revokes what it made instead of leaking it into the next one.
//
// The candidates are tried until one actually decodes, not until one merely exists. MapPicker.cs does
// `LoadTexture(map.PreviewPath) ?? LoadTexture(map.ChartPath)` for the same reason: a Preview.png that
// is empty, truncated or in a format the browser will not decode should fall through to the chart, not
// leave a broken image where the desktop shows a picture.
async function showArtwork(fs, container, map, token) {
    for (const source of [map.previewPath, map.chartPath, map.iconPath]) {
        if (source === null || source === undefined) continue;
        if (!isCurrent(token)) return;

        const url = await fs.objectUrl(source).catch(() => null);
        if (url === null) continue;
        if (!isCurrent(token)) {
            URL.revokeObjectURL(url);
            return;
        }
        artwork.push(url);

        const image = await decode(url);
        if (!isCurrent(token)) return; // clearMaps owns this URL now and has already revoked it
        if (image === null) continue;
        image.alt = "";
        container.append(image);
        return;
    }
}

// Resolves to a loaded <img>, or null if the bytes are not a picture this browser can show.
function decode(url) {
    return new Promise((resolve) => {
        const image = new Image();
        image.addEventListener("load", () => resolve(image.naturalWidth > 0 ? image : null), { once: true });
        image.addEventListener("error", () => resolve(null), { once: true });
        image.src = url;
    });
}

function addFact(label, value) {
    const term = document.createElement("dt");
    term.textContent = label;
    const detail = document.createElement("dd");
    detail.textContent = value;
    ui.summary.append(term, detail);
}

function clearMaps() {
    artObserver?.disconnect();
    artObserver = null;
    artQueue.clear();
    for (const url of artwork) URL.revokeObjectURL(url);
    artwork = [];
    ui.maps.innerHTML = "";
}

function setStatus(text) {
    ui.status.textContent = text;
}

function formatBytes(bytes) {
    const units = ["B", "KB", "MB", "GB"];
    let value = bytes;
    let unit = 0;
    while (value >= 1024 && unit < units.length - 1) {
        value /= 1024;
        unit++;
    }
    return `${value.toFixed(value >= 100 || unit === 0 ? 0 : 1)} ${units[unit]}`;
}
