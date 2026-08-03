// The handful of values the Node side and the browser side of the harness both need.
//
// It has to be its own module: run.mjs runs under Node and seed.mjs imports node:fs, while suite.js is
// loaded by Chromium as a page script — so anything suite.js imports has to be free of Node built-ins.

// The OPFS subtree the suite seeds the install into, and the directory run.mjs's stubbed
// showDirectoryPicker hands back. The two run in different pages, so spelling it twice would mean a
// change on one side surfacing only as a selector timeout on the other.
export const INSTALL_PREFIX = "install";
