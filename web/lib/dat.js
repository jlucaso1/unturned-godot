// A deliberately small reader for Unturned's .dat format — enough for the menu metadata the install
// probe shows, and no more.
//
// core/Dat/DatParser.cs is the real one: it ports the game's own tokenizer, handles nested dictionaries
// and lists, quoted keys and inline-vs-block value precedence, and is covered byte-for-byte against the
// game's files. Nothing here tries to replace it. The probe needs two keys out of a map's English.dat
// (Name, Description), all at the top level, so this reads top-level `key value` pairs and skips over
// nested blocks rather than parsing them. If the browser side ever needs the full grammar it should come
// from core/ compiled to WebAssembly, not from a second hand-written parser drifting against the first.

// Top-level key/value pairs of a .dat document, as a Map. Later keys win, matching DatParser.
export function parseDatTopLevel(text) {
    const values = new Map();
    let depth = 0;

    for (const rawLine of String(text ?? "").split(/\r\n|\r|\n/)) {
        const line = stripComment(rawLine).trim();
        if (line === "") continue;

        // Brackets can open or close on their own line, or trail a key. Track the nesting so keys inside
        // a block are not mistaken for top-level ones.
        const opens = countUnquoted(line, "{") + countUnquoted(line, "[");
        const closes = countUnquoted(line, "}") + countUnquoted(line, "]");
        const wasTopLevel = depth === 0;
        depth = Math.max(0, depth + opens - closes);
        if (!wasTopLevel || opens > 0) continue;

        const [key, value] = splitKeyValue(line);
        if (key !== null) values.set(key, value);
    }

    return values;
}

export function datString(text, key) {
    return parseDatTopLevel(text).get(key) ?? null;
}

// Comments run from an unquoted '/' to end of line.
function stripComment(line) {
    let quoted = false;
    for (let i = 0; i < line.length; i++) {
        const c = line[i];
        if (c === '"') quoted = !quoted;
        else if (c === "/" && !quoted) return line.slice(0, i);
    }
    return line;
}

function countUnquoted(line, character) {
    let quoted = false;
    let count = 0;
    for (const c of line) {
        if (c === '"') quoted = !quoted;
        else if (c === character && !quoted) count++;
    }
    return count;
}

// The key is the first whitespace-delimited word, or a quoted string; the value is the rest of the line,
// which is why a Description keeps its spaces and needs no quoting in the game's own files.
function splitKeyValue(line) {
    if (line.startsWith('"')) {
        const end = line.indexOf('"', 1);
        if (end === -1) return [line.slice(1), ""];
        return [line.slice(1, end), unquote(line.slice(end + 1).trim())];
    }

    const space = line.search(/\s/);
    if (space === -1) return [line, ""];
    return [line.slice(0, space), unquote(line.slice(space + 1).trim())];
}

function unquote(value) {
    return value.length >= 2 && value.startsWith('"') && value.endsWith('"') ? value.slice(1, -1) : value;
}
