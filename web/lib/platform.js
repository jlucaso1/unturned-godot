// Which OS the picked folder came off, as far as anything here needs to care.
//
// Two things depend on it, and neither is cosmetic: which masterbundle variant to look for first
// (UnturnedInstall.BundleSuffixes), and whether a path lookup should fold case. The browser cannot ask
// the filesystem either question, so the user agent is the proxy — a wrong guess costs one extra lookup
// for the first, and is the reason the second is an option a caller can override for the tests.

export function currentPlatform() {
    const ua = globalThis.navigator?.userAgent ?? "";
    if (/Windows/i.test(ua)) return "windows";
    if (/Mac OS X|Macintosh/i.test(ua)) return "mac";
    return "linux";
}

// Whether a path lookup on this host should ignore case. NTFS and APFS-as-shipped both match a request
// for "Level.dat" against a file named "level.dat", which is what the desktop's File.Exists sees there;
// ext4 does not. This only matters for ListingFs, which resolves paths out of its own index — HandleFs
// asks the browser, which asks the OS, and so inherits the real answer without being told.
export function isCaseInsensitiveFilesystem(platform = currentPlatform()) {
    return platform !== "linux";
}
