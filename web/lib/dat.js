// A deliberately small reader for Unturned's .dat format — enough for the menu metadata the install
// probe shows, and no more.
//
// core/Dat/DatParser.cs is the real one: it ports the game's own tokenizer, handles nested dictionaries
// and lists, quoted keys and inline-vs-block value precedence, and is covered byte-for-byte against the
// game's files. Nothing here tries to replace it. The probe needs two keys out of a map's English.dat
// (Name, Description), both at the top level, so this reads top-level `key value` pairs and skips over
// nested blocks rather than parsing them. If the browser side ever needs the full grammar it should come
// from core/ compiled to WebAssembly, not from a second hand-written parser drifting against the first.
//
// Where it *does* overlap, it has to agree, because a map the browser describes differently from the
// catalogue is worse than one it says nothing about. Three rules are therefore copied exactly rather
// than approximated: keys are matched case-insensitively (DatDictionary uses OrdinalIgnoreCase), a value
// runs to the end of its line and a '/' only opens a comment where a token would start (DatParser's
// ReadStringValue consumes the remainder of the line, so a URL in a Description keeps its slashes), and
// `\n`, `\t` and `\<anything>` are decoded (DatParser.Unescape).

// Top-level key/value pairs of a .dat document. Keys are compared case-insensitively and the last
// spelling of a duplicate key wins, both matching DatDictionary.
export class DatValues {
    #values = new Map(); // lowercased key -> value

    set(key, value) {
        this.#values.set(key.toLowerCase(), value);
    }

    get(key) {
        return this.#values.get(String(key).toLowerCase());
    }

    has(key) {
        return this.#values.has(String(key).toLowerCase());
    }

    get size() {
        return this.#values.size;
    }
}

export function parseDatTopLevel(text) {
    const values = new DatValues();
    let depth = 0;

    for (const rawLine of String(text ?? "").split(/\r\n|\r|\n/)) {
        const line = rawLine.trim();
        if (line === "") continue;

        // Structural characters only count where the tokenizer would be looking for a token, which for
        // this subset means the start of a line. Inside a value they are ordinary text: `Nested {` reads
        // as the key `Nested` with the value `{`, which is why the game's own files put the brace on its
        // own line.
        const first = line[0];
        if (first === "/") continue; // comment to end of line
        if (first === "{" || first === "[") {
            depth++;
            continue;
        }
        if (first === "}" || first === "]") {
            depth = Math.max(0, depth - 1);
            continue;
        }

        const { key, value, opensBlock } = splitKeyValue(line);
        if (depth === 0 && !opensBlock && key !== null) values.set(key, value);
        // An inline brace opens a block whose keys are not top-level, even though this line was.
        if (opensBlock) depth++;
    }

    return values;
}

export function datString(text, key) {
    return parseDatTopLevel(text).get(key) ?? null;
}

// The key is the first whitespace-delimited word, or a quoted string; the value is everything after it
// on the line. `opensBlock` marks the inline-brace form, whose contents belong to a nested block.
function splitKeyValue(line) {
    let key;
    let rest;
    if (line.startsWith('"')) {
        const end = findClosingQuote(line, 1);
        if (end === -1) return { key: unescape(line.slice(1)), value: "", opensBlock: false };
        key = unescape(line.slice(1, end));
        rest = line.slice(end + 1).replace(/^[ \t]+/, "");
    } else {
        const space = line.search(/[ \t]/);
        if (space === -1) return { key: line, value: "", opensBlock: false };
        key = line.slice(0, space);
        rest = line.slice(space + 1).replace(/^[ \t]+/, "");
    }

    if (rest === "") return { key, value: "", opensBlock: false };
    if (rest[0] === "{" || rest[0] === "[") return { key, value: "", opensBlock: true };
    return { key, value: readValue(rest), opensBlock: false };
}

// DatParser.ReadStringValue: a quoted value ends at its closing quote, an unquoted one at end of line.
// Either way the escapes are decoded.
function readValue(rest) {
    if (rest[0] !== '"') return unescape(rest);
    const end = findClosingQuote(rest, 1);
    return unescape(end === -1 ? rest.slice(1) : rest.slice(1, end));
}

// The closing quote of a run that started at `from`, skipping escaped quotes.
function findClosingQuote(text, from) {
    for (let i = from; i < text.length; i++) {
        if (text[i] === "\\") {
            i++;
            continue;
        }
        if (text[i] === '"') return i;
    }
    return -1;
}

// DatParser.Unescape: 'n' and 't' become the control characters, and everything else — including '\\'
// and '"' — keeps the character that follows the backslash.
function unescape(value) {
    if (!value.includes("\\")) return value;
    let out = "";
    for (let i = 0; i < value.length; i++) {
        if (value[i] !== "\\" || i + 1 >= value.length) {
            out += value[i];
            continue;
        }
        const next = value[++i];
        out += next === "n" ? "\n" : next === "t" ? "\t" : next;
    }
    return out;
}
