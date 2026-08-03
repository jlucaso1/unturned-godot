// The handful of .NET string semantics this layer has to reproduce exactly.
//
// `core/` compares and trims strings with BCL rules, and the browser is previewing what the game's menu
// will show — so wherever the two overlap, a JavaScript approximation is a wrong answer, not a rounding
// error. These three keep coming up: `OrdinalIgnoreCase` (map ordering, the placeholder name slot, the
// bundled-copy prefix), `char.IsWhiteSpace` (the .dat tokenizer, and `IsNullOrWhiteSpace` behind the
// display-name fallback), and they are wrong in JavaScript in opposite directions. They live here rather
// than in each caller because two copies of a Unicode table is two copies to get out of step.
//
// Every table below was dumped from this repo's own runtime across the whole of Unicode and diffed
// against the browser's built-ins. Regenerate if either side's Unicode version moves.

// char.IsWhiteSpace. Not JavaScript's `\s` and not what `String.prototype.trim` strips: .NET counts
// U+0085 NEXT LINE, which JavaScript does not, and JavaScript strips U+FEFF, which .NET does not. Both
// directions matter — an English.dat whose Name is a lone U+0085 falls back to the folder name on the
// desktop, and one whose Name is a lone U+FEFF does not.
const WHITESPACE = new Set([
    0x0009, 0x000a, 0x000b, 0x000c, 0x000d, 0x0020, 0x0085, 0x00a0, 0x1680, 0x2000, 0x2001, 0x2002, 0x2003,
    0x2004, 0x2005, 0x2006, 0x2007, 0x2008, 0x2009, 0x200a, 0x2028, 0x2029, 0x202f, 0x205f, 0x3000,
]);

export function isWhiteSpace(character) {
    return WHITESPACE.has(character.charCodeAt(0));
}

// string.IsNullOrWhiteSpace. The desktop uses this, not a truthiness check, to decide whether a map's
// localized name is usable — so a name of spaces falls back to the folder name rather than leaving the
// card with a blank heading.
export function isNullOrWhiteSpace(text) {
    if (text === null || text === undefined || text === "") return true;
    for (const character of text) if (!isWhiteSpace(character)) return false;
    return true;
}

// Where JavaScript's toUpperCase and .NET's OrdinalIgnoreCase fold disagree, generated from this repo's
// runtime across all of Unicode and checked exhaustively by web/test/casing.mjs.
//
// This was three hand-written rules before — "skip if the uppercase is longer", "skip these ten code
// points", "skip these two ranges" — and each was found wrong by a reviewer rather than by a test. The
// last one was wrong in a way none of the rules could express: .NET folds U+1F80 to U+1F88, a single
// character, while JavaScript expands it to two, so the length rule left it alone and the desktop did
// not. A rule that has to be corrected every round is a table pretending to be a principle.
//
// Two things it records. First, the code points that do not fold at all: "ß" and the "ﬁ" ligatures
// (whose uppercase is longer), U+0131 dotless i, U+017F long s — which `ToUpperInvariant` maps to 'S'
// while `string.Equals("ſ", "S", OrdinalIgnoreCase)` is false, so those two are not the same function —
// and the recent additions the ordinal casing table has not taken up, above the BMP as well as inside
// it. Note that astral code points are *not* exempt as a class: 260 of them do fold.
const DOES_NOT_FOLD = [
    [0xdf, 0xdf],
    [0x131, 0x131],
    [0x149, 0x149],
    [0x17f, 0x17f],
    [0x19b, 0x19b],
    [0x1f0, 0x1f0],
    [0x264, 0x264],
    [0x390, 0x390],
    [0x3b0, 0x3b0],
    [0x587, 0x587],
    [0x1c8a, 0x1c8a],
    [0x1e96, 0x1e9a],
    [0x1f50, 0x1f50],
    [0x1f52, 0x1f52],
    [0x1f54, 0x1f54],
    [0x1f56, 0x1f56],
    [0x1f88, 0x1f8f],
    [0x1f98, 0x1f9f],
    [0x1fa8, 0x1faf],
    [0x1fb2, 0x1fb2],
    [0x1fb4, 0x1fb4],
    [0x1fb6, 0x1fb7],
    [0x1fbc, 0x1fbc],
    [0x1fc2, 0x1fc2],
    [0x1fc4, 0x1fc4],
    [0x1fc6, 0x1fc7],
    [0x1fcc, 0x1fcc],
    [0x1fd2, 0x1fd3],
    [0x1fd6, 0x1fd7],
    [0x1fe2, 0x1fe4],
    [0x1fe6, 0x1fe7],
    [0x1ff2, 0x1ff2],
    [0x1ff4, 0x1ff4],
    [0x1ff6, 0x1ff7],
    [0x1ffc, 0x1ffc],
    [0xa7cd, 0xa7cd],
    [0xa7cf, 0xa7cf],
    [0xa7d3, 0xa7d3],
    [0xa7d5, 0xa7d5],
    [0xa7db, 0xa7db],
    [0xfb00, 0xfb06],
    [0xfb13, 0xfb17],
    [0x10d70, 0x10d85],
    [0x16ebb, 0x16ed3],
];

// Second, the code points .NET folds to something other than what toUpperCase produces — Greek with
// ypogegrammeni, where the ordinal table has a single-character mapping and Unicode's full mapping does
// not.
const FOLDS_DIFFERENTLY = new Map([
    [0x1f80, 0x1f88],
    [0x1f81, 0x1f89],
    [0x1f82, 0x1f8a],
    [0x1f83, 0x1f8b],
    [0x1f84, 0x1f8c],
    [0x1f85, 0x1f8d],
    [0x1f86, 0x1f8e],
    [0x1f87, 0x1f8f],
    [0x1f90, 0x1f98],
    [0x1f91, 0x1f99],
    [0x1f92, 0x1f9a],
    [0x1f93, 0x1f9b],
    [0x1f94, 0x1f9c],
    [0x1f95, 0x1f9d],
    [0x1f96, 0x1f9e],
    [0x1f97, 0x1f9f],
    [0x1fa0, 0x1fa8],
    [0x1fa1, 0x1fa9],
    [0x1fa2, 0x1faa],
    [0x1fa3, 0x1fab],
    [0x1fa4, 0x1fac],
    [0x1fa5, 0x1fad],
    [0x1fa6, 0x1fae],
    [0x1fa7, 0x1faf],
    [0x1fb3, 0x1fbc],
    [0x1fc3, 0x1fcc],
    [0x1ff3, 0x1ffc],
]);

// The fold OrdinalIgnoreCase performs, one code point at a time.
export function ordinalIgnoreCaseKey(text) {
    let out = "";
    for (const character of text) {
        const code = character.codePointAt(0);
        const remapped = FOLDS_DIFFERENTLY.get(code);
        if (remapped !== undefined) out += String.fromCodePoint(remapped);
        else if (doesNotFold(code)) out += character;
        else out += character.toUpperCase();
    }
    return out;
}

function doesNotFold(code) {
    for (const [from, to] of DOES_NOT_FOLD) if (code >= from && code <= to) return true;
    return false;
}

// StringComparison.OrdinalIgnoreCase ordering: code units compared after that fold, not locale
// collation. The difference shows the moment a map's name carries an accent — locale rules file "Åland"
// next to "Aland", ordinal rules file it after "Zeta".
export function compareOrdinalIgnoreCase(a, b) {
    const left = ordinalIgnoreCaseKey(a);
    const right = ordinalIgnoreCaseKey(b);
    return left < right ? -1 : left > right ? 1 : 0;
}
