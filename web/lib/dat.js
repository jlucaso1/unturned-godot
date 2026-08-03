// Unturned's .dat format, as much of it as reading top-level metadata requires.
//
// This is a port of core/Dat/DatParser.cs — its tokenizer verbatim, and its parser reduced to the one
// thing the install probe needs: the scalar key/value pairs of the root dictionary, for a map's
// English.dat (Name, Description). Nested dictionaries and lists are walked for their structure and
// their contents discarded, because nothing here reads them.
//
// It started smaller than this. A hand-written reader that handled "the easy cases" and skipped brackets
// was wrong about the grammar in a new way in each of three consecutive reviews — a second quoted pair
// that could not open a multi-line run, a stray `]` that ended the document, a nested block that closed
// on its own line, a mismatched closer that leaked a fake Name into the catalogue. Every one of those was
// a rule that reads like an edge case and is really just the grammar; the fix each time was to move one
// step closer to the real parser. Porting it outright is where that was always going, and it is what the
// original of this file said should happen: where the browser overlaps core/, a map it describes
// differently from the catalogue is worse than one it says nothing about.
//
// Line-for-line correspondence with DatParser.cs is the point. Anything clever here is a bug: if the
// grammar is ever unclear, the answer is in that file, and web/test/differential.mjs checks the two agree
// over thousands of generated documents rather than over the cases someone thought to write down.

import { isWhiteSpace } from "./dotnet.js";

// Top-level key/value pairs of a .dat document. DatDictionary is keyed with OrdinalIgnoreCase and Set
// replaces, so the last spelling of a duplicate key wins.
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

export function parseDatTopLevel(text) {
    return parseRoot(tokenize(String(text ?? "")));
}

export function datString(text, key) {
    return parseDatTopLevel(text).get(key) ?? null;
}

// --- Tokenizer ---------------------------------------------------------------------------------------
//
// DatParser.Tokenize. The context stack is what decides whether a bare word is a key or a value, and it
// is not the same as the parser's block nesting: it pushes on every bracket and pops on every closer,
// matched or not, while a parser block only ends on the closer that matches it.

const KEY = 0;
const VALUE = 1;
const OPEN_DICT = 2;
const CLOSE_DICT = 3;
const OPEN_LIST = 4;
const CLOSE_LIST = 5;
const LINE_BREAK = 6;

function tokenize(text) {
    const tokens = [];
    const contextIsList = [false];

    let p = 0;
    const n = text.length;
    if (n >= 1 && text[0] === "﻿") p = 1; // skip UTF-8 BOM

    while (p < n) {
        const c = text[p];

        if (c === "/") {
            while (p < n && text[p] !== "\n" && text[p] !== "\r") p++;
        } else if (c === "\r" || c === "\n") {
            tokens.push({ type: LINE_BREAK, text: "" });
            if (c === "\r" && p + 1 < n && text[p + 1] === "\n") p++;
            p++;
        } else if (c === "{") {
            tokens.push({ type: OPEN_DICT, text: "" });
            contextIsList.push(false);
            p = consumeBracket(text, p);
        } else if (c === "}") {
            tokens.push({ type: CLOSE_DICT, text: "" });
            if (contextIsList.length > 1) contextIsList.pop();
            p = consumeBracket(text, p);
        } else if (c === "[") {
            tokens.push({ type: OPEN_LIST, text: "" });
            contextIsList.push(true);
            p = consumeBracket(text, p);
        } else if (c === "]") {
            tokens.push({ type: CLOSE_LIST, text: "" });
            if (contextIsList.length > 1) contextIsList.pop();
            p = consumeBracket(text, p);
        } else if (isWhiteSpace(c) || c === ",") {
            p++; // commas are treated as whitespace
        } else if (contextIsList[contextIsList.length - 1]) {
            const value = readStringValue(text, p);
            p = value.next;
            tokens.push({ type: VALUE, text: value.text });
        } else {
            const key = readKey(text, p);
            p = key.next;
            tokens.push({ type: KEY, text: key.text });
            while (p < n && (text[p] === " " || text[p] === "\t")) p++;
            if (p < n && text[p] !== "\r" && text[p] !== "\n") {
                const value = readStringValue(text, p);
                p = value.next;
                tokens.push({ type: VALUE, text: value.text });
            }
        }
    }
    return tokens;
}

// A bracket swallows one comma tight against it.
function consumeBracket(text, p) {
    p++;
    if (p < text.length && text[p] === ",") p++;
    return p;
}

// DatParser.ReadKey: a quoted run, or everything up to the next whitespace — brackets and commas included,
// so `Name{` is one key.
function readKey(text, p) {
    if (text[p] === '"') return readQuoted(text, p);
    const start = p;
    while (p < text.length && !isWhiteSpace(text[p])) p++;
    return { text: text.slice(start, p), next: p };
}

// DatParser.ReadStringValue: a quoted run, or the rest of the line, escapes decoded either way.
function readStringValue(text, p) {
    if (text[p] === '"') return readQuoted(text, p);
    const start = p;
    let out = "";
    let plain = true;
    while (p < text.length && text[p] !== "\r" && text[p] !== "\n") {
        const c = text[p];
        if (c === "\\" && p + 1 < text.length) {
            if (plain) {
                out = text.slice(start, p);
                plain = false;
            }
            p++;
            out += unescape(text[p]);
        } else if (!plain) {
            out += c;
        }
        p++;
    }
    return { text: plain ? text.slice(start, p) : out, next: p };
}

// DatParser.ReadQuoted: runs to the first unescaped quote, crossing newlines to get there, and takes one
// comma tight against the closing quote with it.
function readQuoted(text, p) {
    p++; // opening quote
    const start = p;
    let out = "";
    let plain = true;
    while (p < text.length && text[p] !== '"') {
        const c = text[p];
        if (c === "\\" && p + 1 < text.length) {
            if (plain) {
                out = text.slice(start, p);
                plain = false;
            }
            p++;
            out += unescape(text[p]);
        } else if (!plain) {
            out += c;
        }
        p++;
    }
    const text_ = plain ? text.slice(start, p) : out;
    if (p < text.length) p++; // closing quote
    if (p < text.length && text[p] === ",") p++;
    return { text: text_, next: p };
}

// DatParser.Unescape: 'n' and 't' become the control characters, everything else keeps the character
// that followed the backslash.
function unescape(c) {
    return c === "n" ? "\n" : c === "t" ? "\t" : c;
}

// --- Parser ------------------------------------------------------------------------------------------
//
// ParseDictionaryBody(root: true), with the sub-structures walked iteratively instead of recursively.
// Only the root's scalars are kept, so nothing below the top level needs a value — but it does need its
// nesting followed exactly, because that is what decides when the top level resumes.
//
// Three rules here are worth stating, because each was a bug before it was a rule:
//
//   * A block only ends on the closer that MATCHES it. ParseListBody returns on CloseList and
//     ParseDictionaryBody on CloseDict; the other one falls through to `i++` as a stray token. So
//     `Metadata [ } Name Fake ]` keeps `Name Fake` inside the list, where the desktop discards it.
//   * A bracket only OPENS a block where the grammar expects a value: after a key, or as an item of a
//     list. Anywhere else it is a stray token that opens nothing, which is why `{ Name X` leaves Name at
//     the top level and the `}` after it ends the document.
//   * Only CloseDict ends the root body. A stray CloseList at the root is skipped, and the metadata
//     under it is still read.

function parseRoot(tokens) {
    const values = new DatValues();
    // One entry per open block, true for a list. Empty means the root dictionary body.
    const blocks = [];
    let i = 0;

    while (i < tokens.length) {
        const token = tokens[i];
        const inList = blocks.length > 0 && blocks[blocks.length - 1];

        if (inList) {
            if (token.type === CLOSE_LIST) {
                blocks.pop();
                i++;
            } else if (token.type === OPEN_DICT) {
                blocks.push(false);
                i++;
            } else if (token.type === OPEN_LIST) {
                blocks.push(true);
                i++;
            } else {
                i++; // values, stray closers and line breaks alike
            }
            continue;
        }

        // A dictionary body: the root's, or a nested one.
        if (token.type === CLOSE_DICT) {
            if (blocks.length === 0) return values; // the root body returns, ending the document
            blocks.pop();
            i++;
            continue;
        }

        if (token.type !== KEY) {
            i++; // tolerate stray tokens, including a CloseList and an unexpected bracket
            continue;
        }

        i++;
        let inline = "";
        if (i < tokens.length && tokens[i].type === VALUE) {
            inline = tokens[i].text;
            i++;
        }

        // A '{' or '[' on the following line overrides an inline value, and only line breaks may come
        // between. Whatever the key held, a block replaces it — so it stops being a scalar.
        let j = i;
        while (j < tokens.length && tokens[j].type === LINE_BREAK) j++;
        const opens = j < tokens.length ? tokens[j].type : -1;
        if (opens === OPEN_DICT || opens === OPEN_LIST) {
            i = j + 1;
            blocks.push(opens === OPEN_LIST);
            if (blocks.length === 1) values.delete(token.text);
        } else if (blocks.length === 0) {
            values.set(token.text, inline);
        }
    }

    return values;
}
