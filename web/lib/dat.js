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
//   * A comma after a closing quote depends on which token closed: after a quoted KEY only a tight one
//     is consumed, after a quoted VALUE the tokenizer loop skips detached ones too. See afterQuotedKey.
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

// Brackets are their own tokens, so they are peeled off the front of a line rather than treated as a
// property of the line. Every rule below was checked by running the game's own parser:
//
//   Nested {      ->  [Nested] = "{", and a following `Name Leak` is STILL top-level. A bracket is only
//   Name Leak         structural where the tokenizer would start a token, and after a key it is just
//   }                 the value; the `}` then closes a block nothing opened, which ends the document.
//
//   Nested        ->  [Nested] is not a value at all. A block opening on the next line replaces the
//   {                 inline value the key would otherwise have had, and its contents are one level in.
//
//   { Name X      ->  [Name] = "X". ParseDictionaryBody(root: true) consumes the root dictionary's own
//                     opening brace and keeps tokenizing the same line.
//
//   [             ->  a root LIST, not a tolerated root brace: its contents are values, not keys, and
//   Name Fake         the `]` returns to the root rather than ending the document — a `Name Real` after
//   ]                 it is read normally.
//   Name Real
//
//   ]             ->  [Name] = "Real". The two closers are not symmetric: ParseDictionaryBody returns
//   Name Real         only on CloseDict, so an unmatched `]` is skipped as a stray token while an
//                     unmatched `}` ends the document.
export function parseDatTopLevel(text) {
    const values = new DatValues();
    // One entry per open bracket, true for a list. Depth alone is not enough: inside a *dictionary*
    // `Name "Inside"` is a key with a quoted value, so a `}` after it is still a token, while inside a
    // *list* the whole of `Name "Inside" ]` is one value running to the end of the line.
    const open = [];
    // The last key set at the top level, which a block opening next takes back over.
    let pending = null;
    let documentEnded = false;

    for (const rawLine of logicalLines(String(text ?? ""))) {
        // One loop, not a pass per token kind: what a quoted value leaves behind goes back through the
        // bracket handling rather than straight to the next pair. `Name "Temporary" {` is a key whose
        // block *replaces* its scalar — [Name] stops being a value — and sending that `{` to
        // splitKeyValue would make it a key of its own and leave the block's contents at the top level.
        let rest = trimTokenGap(rawLine);

        while (rest !== "") {
            const first = rest[0];

            // A comment, but only here: after a key ReadStringValue is entered directly, so the '/' in
            // `Name / note` is the start of the value rather than a comment.
            if (first === "/") break;

            if (first === "{" || first === "[") {
                // ParseDictionaryBody(root: true) skips a stray OpenDict like any other token it did
                // not expect, so a brace with no key in front of it opens nothing and whatever follows
                // is still top-level. Anything else opens a block — and one opening after a key
                // replaces the inline value that key would otherwise have had.
                const stray = first === "{" && open.length === 0 && pending === null;
                if (!stray) {
                    if (open.length === 0 && pending !== null) values.delete(pending);
                    open.push(first === "[");
                    pending = null;
                }
                rest = trimTokenGap(rest.slice(1));
                continue;
            }

            if (first === "}" || first === "]") {
                if (open.length === 0) {
                    // Only CloseDict returns from the root body. A stray CloseList is skipped like any
                    // other unexpected token, so a hand-edited file with an unmatched ']' above its
                    // metadata still yields the Name and Description below it.
                    if (first === "}") {
                        documentEnded = true;
                        break;
                    }
                    pending = null;
                    rest = trimTokenGap(rest.slice(1));
                    continue;
                }
                open.pop();
                pending = null;
                rest = trimTokenGap(rest.slice(1));
                continue;
            }

            // Inside a block the contents are not top-level, and this subset does not read them — but it
            // still has to find where each one ENDS, because that is where a closing bracket later on
            // the same line becomes visible again.
            if (open.length > 0) {
                rest = skipNested(rest, open[open.length - 1]);
                continue;
            }

            // A line can hold more than one pair. ReadQuoted stops at its closing quote and the
            // tokenizer picks up right after it, so `Name "Map" Description "Blurb"` is two keys —
            // while an *unquoted* value runs to the end of the line, so `Name Map Description Blurb` is
            // one key whose value is "Map Description Blurb".
            const { key, value, remainder } = splitKeyValue(rest);
            if (key === null) break;
            values.set(key, value);
            pending = key;
            // Defensive: a remainder that did not shrink would spin here forever.
            if (remainder.length >= rest.length) break;
            rest = trimTokenGap(remainder);
        }

        if (documentEnded) break;
    }

    return values;
}

// The gap between two tokens. The tokenizer's whitespace branch takes commas with it, so `Name "Map" ,
// Description "Blurb"` is two pairs and a line may open with a comma.
function trimTokenGap(text) {
    return text.replace(/^[ \t\r\n,]+/, "");
}

// One token's worth of a block's contents, returning what is left of the line. Only where the token ends
// matters here: a quoted run ends at its closing quote and the tokenizer carries on, while an unquoted
// one runs to the end of the line and swallows any bracket on the way. That is the whole reason
// `{ Name "Inside" }` closes on its line and `{ Name Inside }` does not.
function skipNested(rest, inList) {
    if (rest[0] === '"') {
        // In a list that run was a value and the next token starts after it. In a dictionary it was a
        // key, whose value follows on the same line.
        const end = findClosingQuote(rest, 1);
        if (end === -1) return "";
        return inList ? afterQuotedValue(rest, end) : skipNestedValue(afterQuotedKey(rest, end));
    }
    // An unquoted list value is read by ReadStringValue, which stops only at the newline.
    if (inList) return "";
    const space = rest.search(/[ \t]/);
    if (space === -1) return "";
    return skipNestedValue(rest.slice(space + 1).replace(/^[ \t]+/, ""));
}

// The value of a key inside a block. Only a quoted one ends before the newline does.
function skipNestedValue(rest) {
    if (rest === "" || rest[0] !== '"') return "";
    const end = findClosingQuote(rest, 1);
    return end === -1 ? "" : afterQuotedValue(rest, end);
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
// A quote only opens a run where a *token* starts — before the key, or before the value. Inside an
// already-unquoted token it is an ordinary character: ReadStringValue scans an unquoted value to the
// newline without caring about quotes, so `Description About 12" wide` ends at that line and the next
// line is still a key. Treating any quote as an opener would splice the two together.
const BEFORE_KEY = 0;
const IN_KEY = 1;
const BEFORE_VALUE = 2;
const IN_VALUE = 3;

function* logicalLines(text) {
    let start = 0;
    let quoted = false;
    let where = BEFORE_KEY;

    for (let i = 0; i < text.length; i++) {
        const c = text[i];

        if (quoted) {
            if (c === "\\")
                i++; // an escaped character, including \" , is not a delimiter
            else if (c === '"') {
                quoted = false;
                // The run just closed. A quoted KEY is followed by its value; a quoted VALUE returns
                // control to the tokenizer loop, where the next token is a fresh key — so the state has
                // to go all the way back, not to IN_VALUE. Stopping short left `Name "Map" Description
                // "First<newline>Second"` unable to see the second opening quote, splitting a value the
                // game reads whole.
                where = where === BEFORE_KEY ? BEFORE_VALUE : BEFORE_KEY;
            }
            continue;
        }

        if (c === "\n" || c === "\r") {
            yield text.slice(start, i);
            if (c === "\r" && text[i + 1] === "\n") i++;
            start = i + 1;
            where = BEFORE_KEY;
            continue;
        }

        if (c === " " || c === "\t") {
            // Whitespace ends the key and puts us in front of the value.
            if (where === IN_KEY) where = BEFORE_VALUE;
            continue;
        }

        // A '/' where a token would start opens a comment; the rest of the line is not tokenized.
        if (c === "/" && where === BEFORE_KEY) {
            while (i + 1 < text.length && text[i + 1] !== "\n" && text[i + 1] !== "\r") i++;
            continue;
        }

        if (c === '"' && (where === BEFORE_KEY || where === BEFORE_VALUE)) {
            quoted = true;
            continue;
        }

        // Between tokens the tokenizer treats a comma as whitespace, so it does not start a key.
        if (c === "," && where === BEFORE_KEY) continue;
        // ReadQuoted swallows one comma right after a closing quote, so it does not start the value.
        if (c === "," && where === BEFORE_VALUE && text[i - 1] === '"') continue;

        where = where === BEFORE_KEY ? IN_KEY : where === BEFORE_VALUE ? IN_VALUE : where;
    }

    if (start < text.length) yield text.slice(start);
}

// One key/value pair off the front of a line, plus whatever is left to keep reading. The key is the
// first whitespace-delimited word or a quoted string; the value is everything after it on the line,
// brace or not — ReadStringValue is reached unconditionally once a key has been read.
function splitKeyValue(line) {
    let key;
    let rest;
    if (line.startsWith('"')) {
        const end = findClosingQuote(line, 1);
        if (end === -1) return { key: unescape(line.slice(1)), value: "", remainder: "" };
        key = unescape(line.slice(1, end));
        rest = afterQuotedKey(line, end);
    } else {
        const space = line.search(/[ \t]/);
        if (space === -1) return { key: line, value: "", remainder: "" };
        key = line.slice(0, space);
        rest = line.slice(space + 1).replace(/^[ \t]+/, "");
    }

    if (rest === "") return { key, value: "", remainder: "" };
    const { value, remainder } = readValue(rest);
    return { key, value, remainder };
}

// After a quoted KEY, ReadStringValue is entered directly and skips nothing, so only a comma tight
// against the closing quote (the one ReadQuoted itself swallows) is consumed:
//
//     "Name", "Map Name"   ->  [Name] = "Map Name"
//     "Name" , "Map Name"  ->  [Name] = ", \"Map Name\""
//
// After a quoted VALUE, control returns to the tokenizer loop instead, and that loop treats a comma as
// whitespace — so a detached one before the next key is skipped too:
//
//     Name "Map" , Description "Blurb"  ->  two keys
function afterQuotedKey(text, end) {
    let after = end + 1;
    if (text[after] === ",") after++;
    return text.slice(after).replace(/^[ \t]+/, "");
}

function afterQuotedValue(text, end) {
    return text.slice(end + 1).replace(/^[ \t,]+/, "");
}

// DatParser.ReadStringValue: a quoted value ends at its closing quote and the tokenizer carries on from
// there; an unquoted one runs to the end of the line and leaves nothing behind. Either way escapes are
// decoded.
function readValue(rest) {
    if (rest[0] !== '"') return { value: unescape(rest), remainder: "" };
    const end = findClosingQuote(rest, 1);
    if (end === -1) return { value: unescape(rest.slice(1)), remainder: "" };
    return { value: unescape(rest.slice(1, end)), remainder: afterQuotedValue(rest, end) };
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
