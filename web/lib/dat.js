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
// catalogue is worse than one it says nothing about. The rules below are therefore copied rather than
// approximated, and every one of them was confirmed by running the game's parser rather than reasoned
// from its source — the two disagreed more than once:
//
//   * Keys match case-insensitively and the last duplicate wins (DatDictionary, OrdinalIgnoreCase).
//   * A value runs to the end of its line, and '/' only opens a comment where a token would start, so a
//     URL in a Description keeps its slashes (ReadStringValue).
//   * `\n`, `\t` and `\<anything>` are decoded (Unescape), and a quoted run ends at the first
//     unescaped quote — crossing newlines to get there (ReadQuoted).
//   * One comma directly after a closing quote belongs to the quote, not to what follows (ReadQuoted).
//   * Braces are structural only at the start of a line; see parseDatTopLevel.

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

    delete(key) {
        this.#values.delete(String(key).toLowerCase());
    }

    get size() {
        return this.#values.size;
    }
}

// Three rules, each checked by running the game's own parser rather than reasoned from the source:
//
//   Nested {      ->  [Nested] = "{", and a following `Name Leak` is STILL top-level. A brace is only
//   Name Leak         structural where the tokenizer would start a token, and after a key it is just
//   }                 the value; the `}` then closes a block nothing opened, which ends the document.
//
//   Nested        ->  [Nested] is not a value at all. A block opening on the next line replaces the
//   {                 inline value the key would otherwise have had, and its contents are one level in.
//
// The second is why the game's files put braces on their own line, and the first is why a hand-edited
// file that does not can hide the rest of its keys — from the desktop and from here alike.
export function parseDatTopLevel(text) {
    const values = new DatValues();
    let depth = 0;
    // The last key set at the top level, which a block opening on the next line takes back over.
    let pending = null;

    for (const rawLine of logicalLines(String(text ?? ""))) {
        const line = rawLine.trim();
        if (line === "") continue;

        const first = line[0];
        if (first === "/") continue; // comment to end of line

        if (first === "{" || first === "[") {
            if (depth === 0 && pending !== null) values.delete(pending);
            depth++;
            pending = null;
            continue;
        }

        if (first === "}" || first === "]") {
            // A close with nothing open ends the root dictionary, and with it the document.
            if (depth === 0) break;
            depth--;
            pending = null;
            continue;
        }

        if (depth > 0) {
            pending = null;
            continue;
        }

        const { key, value } = splitKeyValue(line);
        if (key === null) continue;
        values.set(key, value);
        pending = key;
    }

    return values;
}

export function datString(text, key) {
    return parseDatTopLevel(text).get(key) ?? null;
}

// Splits into lines the way the tokenizer would, which is not the same as splitting on newlines:
// ReadQuoted runs to its closing quote and crosses CR/LF on the way, so
//
//     Name "First
//     Second"
//
// is one key with a two-line value, and an *unterminated* quote swallows the rest of the document.
// Splitting first and parsing after would read the continuation as another key. Comments are handled
// here too, because a stray quote inside one ("/ he said "hi") must not open a quoted run — the game's
// tokenizer consumes a comment to end of line without looking at what is in it.
function* logicalLines(text) {
    let start = 0;
    let quoted = false;
    let lineHasContent = false;

    for (let i = 0; i < text.length; i++) {
        const c = text[i];

        if (quoted) {
            if (c === "\\")
                i++; // an escaped character, including \" , is not a delimiter
            else if (c === '"') quoted = false;
            continue;
        }

        if (c === "\n" || c === "\r") {
            yield text.slice(start, i);
            if (c === "\r" && text[i + 1] === "\n") i++;
            start = i + 1;
            lineHasContent = false;
            continue;
        }

        if (c === " " || c === "\t") continue;

        // A '/' where a token would start opens a comment; the rest of the line is not tokenized.
        if (c === "/" && !lineHasContent) {
            while (i + 1 < text.length && text[i + 1] !== "\n" && text[i + 1] !== "\r") i++;
            continue;
        }

        lineHasContent = true;
        if (c === '"') quoted = true;
    }

    if (start < text.length) yield text.slice(start);
}

// The key is the first whitespace-delimited word, or a quoted string; the value is everything after it on
// the line, brace or not — ReadStringValue is reached unconditionally once a key has been read.
function splitKeyValue(line) {
    let key;
    let rest;
    if (line.startsWith('"')) {
        const end = findClosingQuote(line, 1);
        if (end === -1) return { key: unescape(line.slice(1)), value: "" };
        key = unescape(line.slice(1, end));
        // ReadQuoted swallows one comma that immediately follows the closing quote, which is what makes
        // `"Name", "Map Name"` a key and a value rather than a key and a value starting with a comma.
        // Only immediately: a space before the comma leaves it in the value, there as here.
        let after = end + 1;
        if (line[after] === ",") after++;
        rest = line.slice(after).replace(/^[ \t]+/, "");
    } else {
        const space = line.search(/[ \t]/);
        if (space === -1) return { key: line, value: "" };
        key = line.slice(0, space);
        rest = line.slice(space + 1).replace(/^[ \t]+/, "");
    }

    return { key, value: rest === "" ? "" : readValue(rest) };
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
