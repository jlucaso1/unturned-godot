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

// DatTokenizer.EContext. The stack starts EMPTY and getContext falls back to a dictionary, so a closer
// with nothing to close leaves the context alone instead of unwinding past the root.
const DICTIONARY = 0;
const LIST = 1;

function tokenize(text) {
    const tokens = [];
    const contextStack = [];

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
            contextStack.push(DICTIONARY);
            p = consumeBracket(text, p);
        } else if (c === "}") {
            popContext(contextStack, DICTIONARY);
            tokens.push({ type: CLOSE_DICT, text: "" });
            p = consumeBracket(text, p);
        } else if (c === "[") {
            tokens.push({ type: OPEN_LIST, text: "" });
            contextStack.push(LIST);
            p = consumeBracket(text, p);
        } else if (c === "]") {
            popContext(contextStack, LIST);
            tokens.push({ type: CLOSE_LIST, text: "" });
            p = consumeBracket(text, p);
        } else if (isWhiteSpace(c)) {
            // No comma case here, deliberately. DatTokenizer's main loop has none: it eats a comma only
            // where one is tight against a bracket or a closing quote. A comma anywhere else is an
            // ordinary character, so `,Name X` has ",Name" for its key.
            p++;
        } else if (getContext(contextStack) === LIST) {
            const value = readStringValue(text, p);
            p = value.next;
            tokens.push({ type: VALUE, text: value.text });
        } else {
            const key = readKey(text, p);
            p = key.next;
            tokens.push({ type: KEY, text: key.text });
            while (p < n && (text[p] === " " || text[p] === "\t")) p++;
            // The gate is !IsWhiteSpace, not "not a line break". Only spaces and tabs were skipped
            // above, so any OTHER whitespace — a vertical tab, a non-breaking space — is still sitting
            // there, and no value starts on it: the key gets none and the next word becomes a key.
            if (p < n && !isWhiteSpace(text[p])) {
                const value = readStringValue(text, p);
                p = value.next;
                tokens.push({ type: VALUE, text: value.text });
            }
        }
    }
    return tokens;
}

// DatTokenizer.GetContext: an empty stack reads as a dictionary, which is what makes the root a
// dictionary body without anything having opened it.
function getContext(stack) {
    return stack.length > 0 ? stack[stack.length - 1] : DICTIONARY;
}

// DatTokenizer.PopContext: a closer unwinds the stack ONLY when it matches the top of it. That is the
// difference between `[ } foo bar ]` reading `foo bar` as a list value — which is what the game does,
// because the '}' finds a list on top and leaves it there — and reading it as a key and a value, which
// is what popping unconditionally produces.
function popContext(stack, expected) {
    if (stack.length > 0 && stack[stack.length - 1] === expected) stack.pop();
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
    let scan = p;
    while (scan < text.length && text[scan] !== "\r" && text[scan] !== "\n" && text[scan] !== "\\") scan++;
    if (scan >= text.length || text[scan] !== "\\") return { text: text.slice(start, scan), next: scan };

    let out = text.slice(start, scan);
    p = scan;
    while (p < text.length) {
        const c = text[p];
        if (c === "\r" || c === "\n") break;
        if (c === "\\") {
            p++;
            // A backslash with nothing after it is DROPPED: the game sets escapeNextChar, reads past the
            // end of input and its do/while exits on !hasChar before anything is appended.
            if (p >= text.length) break;
            out += unescape(text[p], false);
            p++;
            continue;
        }
        out += c;
        p++;
    }
    return { text: out, next: p };
}

// DatParser.ReadQuoted: runs to the first unescaped quote, crossing newlines to get there, and takes one
// comma tight against the closing quote with it.
function readQuoted(text, p) {
    p++; // opening quote
    const start = p;
    let scan = p;
    while (scan < text.length && text[scan] !== '"' && text[scan] !== "\\") scan++;
    if (scan >= text.length || text[scan] !== "\\") {
        p = scan;
        if (p < text.length) p++; // closing quote
        if (p < text.length && text[p] === ",") p++;
        return { text: text.slice(start, scan), next: p };
    }

    let out = text.slice(start, scan);
    p = scan;
    while (p < text.length && text[p] !== '"') {
        const c = text[p];
        if (c === "\\") {
            p++;
            if (p >= text.length) break; // trailing backslash at end of input, dropped as above
            out += unescape(text[p], true);
            p++;
            continue;
        }
        out += c;
        p++;
    }
    if (p < text.length) p++; // closing quote
    if (p < text.length && text[p] === ",") p++;
    return { text: out, next: p };
}

// The escape table both readers share. 'n' and 't' become the control characters and a doubled backslash
// is one backslash — but ANY OTHER escape KEEPS the backslash that introduced it, and the game logs
// "unrecognized escape sequence".
//
// That re-attachment is not incidental: 3.23.7.0 added '\n' handling to UNQUOTED strings, which broke
// the mods writing Windows paths, and this is the workaround SDG shipped for them. So `Some\Path` stays
// `Some\Path` rather than collapsing to `SomePath`.
//
// A quoted run also recognizes \" ; an unquoted one does not, because a bare '"' does not end one.
function unescape(c, quoted) {
    if (c === "n") return "\n";
    if (c === "t") return "\t";
    if (c === "\\") return "\\";
    if (c === '"' && quoted) return '"';
    return "\\" + c;
}

// --- Parser ------------------------------------------------------------------------------------------
//
// ParseDictionaryBody(root: true), with the sub-structures walked iteratively instead of recursively.
// Only the root's scalars are kept, so nothing below the top level needs a value — but it does need its
// nesting followed exactly, because that is what decides when the top level resumes.
//
// Four rules here are worth stating, because each was a bug before it was a rule:
//
//   * A block only ends on the closer that MATCHES it. ParseListBody returns on CloseList and
//     ParseDictionaryBody on CloseDict; the other one falls through to `i++` as a stray token. So
//     `Metadata [ } Name Fake ]` keeps `Name Fake` inside the list, where the desktop discards it.
//   * A bracket only OPENS a block where the grammar expects a value: after a key, or as an item of a
//     list. Anywhere else it is a stray token that opens nothing, which is why `{ Name X` leaves Name at
//     the top level.
//   * NOTHING ends the root body. DatParser.Parse's own loop switches on Key and Comment only, so a
//     CloseDictionary falls through to `default:` and merely advances — the root has no closer to find.
//     One unbalanced '}' half way down a workshop asset costs a token, not the rest of the file.
//   * A bracket overriding an inline value must be at most ONE line break away. ReadDictionaryValue
//     advances past a single LineBreak before it looks, so a blank line in between means the bracket is
//     not that key's value at all: the key stays a scalar and the block is parsed as if unintroduced.

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

        // A dictionary body: the root's, or a nested one. Only a NESTED one can be closed; at the root
        // the same token is a stray that advances and nothing more.
        if (token.type === CLOSE_DICT) {
            if (blocks.length > 0) blocks.pop();
            i++;
            continue;
        }

        if (token.type !== KEY) {
            i++; // tolerate stray tokens, including a CloseList and an unexpected bracket
            continue;
        }

        i++;
        // Null, not "", for a key with no value at all: ReadDictionaryValue builds DatValue(null) when
        // the token after the key was not a Value.
        let inline = null;
        if (i < tokens.length && tokens[i].type === VALUE) {
            inline = tokens[i].text;
            i++;
        }

        // A '{' or '[' on the following line overrides an inline value, and AT MOST ONE line break may
        // come between. Whatever the key held, a block replaces it — so it stops being a scalar.
        let j = i;
        if (j < tokens.length && tokens[j].type === LINE_BREAK) j++;
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
