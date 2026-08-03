// The handful of .NET string semantics this layer has to reproduce exactly.
//
// `core/` compares and trims strings with BCL rules, and the browser is previewing what the game's menu
// will show — so wherever the two overlap, a JavaScript approximation is a wrong answer, not a rounding
// error. These three keep coming up: `OrdinalIgnoreCase` (map ordering, the placeholder name slot, the
// bundled-copy prefix), `char.IsWhiteSpace` (the .dat tokenizer, and `IsNullOrWhiteSpace` behind the
// display-name fallback), and they are wrong in JavaScript in opposite directions. They live here rather
// than in each caller because two copies of a Unicode table is two copies to get out of step.
//
// Every table below was dumped from this repo's own runtime across the whole BMP and diffed against the
// browser's built-ins. Regenerate if either side's Unicode version moves.

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

// The ten code points where JavaScript's toUpperCase disagrees with the fold OrdinalIgnoreCase performs,
// *without* changing length. U+0131 (dotless i) is the well-known one; U+017F (long s) is the subtle one
// — `ToUpperInvariant` maps it to 'S', yet `string.Equals("ſ", "S", OrdinalIgnoreCase)` is false, so the
// ordinal fold and the invariant upcase are not the same function. It is the only BMP code point where
// they part company, and modelling the fold on ToUpperInvariant alone merged `Deſcription` with
// `Description`. The rest are recent Unicode additions the ordinal table has not taken up.
const NOT_FOLDED_BY_ORDINAL = new Set([
    0x0131, 0x017f, 0x019b, 0x0264, 0x1c8a, 0xa7cd, 0xa7cf, 0xa7d3, 0xa7d5, 0xa7db,
]);

// The upcase OrdinalIgnoreCase performs, one code point at a time. Two guards, because JavaScript's
// toUpperCase does the full current Unicode mapping and the ordinal table does not: a code point whose
// uppercase is *longer* than itself is left alone (102 of those, "ß" and the "ﬁ" ligatures among them),
// and so are the ten above. Without both, "ß"/"SS" and "ı"/"I" would compare equal here and distinct on
// the desktop.
export function ordinalIgnoreCaseKey(text) {
    let out = "";
    for (const character of text) {
        const upper = character.toUpperCase();
        const folds =
            upper.length === character.length && !NOT_FOLDED_BY_ORDINAL.has(character.codePointAt(0));
        out += folds ? upper : character;
    }
    return out;
}

// StringComparison.OrdinalIgnoreCase ordering: code units compared after that fold, not locale
// collation. The difference shows the moment a map's name carries an accent — locale rules file "Åland"
// next to "Aland", ordinal rules file it after "Zeta".
export function compareOrdinalIgnoreCase(a, b) {
    const left = ordinalIgnoreCaseKey(a);
    const right = ordinalIgnoreCaseKey(b);
    return left < right ? -1 : left > right ? 1 : 0;
}
