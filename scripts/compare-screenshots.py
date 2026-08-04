#!/usr/bin/env python3
"""Pixel-compare two PNG screenshots and report how much of the frame moved.

An optimization that is meant to be invisible has to prove it. Godot writes screenshots with
`Image.SavePng`, so both sides of an A/B are 8-bit non-interlaced PNGs and can be compared exactly,
without ImageMagick or Pillow — neither of which is present on a bare agent container.

    ./scripts/compare-screenshots.py before.png after.png
    ./scripts/compare-screenshots.py before.png after.png --diff diff.png --max-percent 0.01

Prints one line per comparison plus a summary, and exits non-zero when the differing share of the
frame exceeds --max-percent (default: report only, never fail). --tolerance ignores per-channel
deltas at or below a threshold, for the rare case where a change is provably a rounding difference;
leave it at 0 unless you can say why.

The diff image is the absolute per-channel difference, amplified so a one-step delta is visible.
"""

import argparse
import struct
import sys
import zlib


class PngError(Exception):
    pass


def _read_chunks(data):
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise PngError("not a PNG")
    offset = 8
    while offset < len(data):
        (length,) = struct.unpack(">I", data[offset:offset + 4])
        kind = data[offset + 4:offset + 8]
        body = data[offset + 8:offset + 8 + length]
        yield kind, body
        offset += 12 + length


def _paeth(a, b, c):
    p = a + b - c
    pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
    if pa <= pb and pa <= pc:
        return a
    return b if pb <= pc else c


def read_png(path):
    """Decode an 8-bit non-interlaced RGB/RGBA/grey PNG to (width, height, channels, bytearray)."""
    with open(path, "rb") as handle:
        data = handle.read()

    width = height = depth = colour = None
    idat = bytearray()
    for kind, body in _read_chunks(data):
        if kind == b"IHDR":
            width, height, depth, colour, _, _, interlace = struct.unpack(">IIBBBBB", body)
            if depth != 8:
                raise PngError(f"{path}: bit depth {depth}, only 8 is supported")
            if interlace:
                raise PngError(f"{path}: interlaced PNGs are not supported")
            if colour not in (0, 2, 6):
                raise PngError(f"{path}: colour type {colour}, only 0/2/6 are supported")
        elif kind == b"IDAT":
            idat += body
        elif kind == b"IEND":
            break

    if width is None:
        raise PngError(f"{path}: no IHDR")

    channels = {0: 1, 2: 3, 6: 4}[colour]
    stride = width * channels
    raw = zlib.decompress(bytes(idat))
    if len(raw) != (stride + 1) * height:
        raise PngError(f"{path}: {len(raw)} bytes of scanline data, expected {(stride + 1) * height}")

    out = bytearray(stride * height)
    pos = 0
    for y in range(height):
        filter_type = raw[pos]
        pos += 1
        line = raw[pos:pos + stride]
        pos += stride
        base = y * stride
        prior = base - stride
        if filter_type == 0:
            out[base:base + stride] = line
        elif filter_type == 1:
            for x in range(stride):
                left = out[base + x - channels] if x >= channels else 0
                out[base + x] = (line[x] + left) & 0xFF
        elif filter_type == 2:
            for x in range(stride):
                up = out[prior + x] if y else 0
                out[base + x] = (line[x] + up) & 0xFF
        elif filter_type == 3:
            for x in range(stride):
                left = out[base + x - channels] if x >= channels else 0
                up = out[prior + x] if y else 0
                out[base + x] = (line[x] + ((left + up) >> 1)) & 0xFF
        elif filter_type == 4:
            for x in range(stride):
                left = out[base + x - channels] if x >= channels else 0
                up = out[prior + x] if y else 0
                upleft = out[prior + x - channels] if y and x >= channels else 0
                out[base + x] = (line[x] + _paeth(left, up, upleft)) & 0xFF
        else:
            raise PngError(f"{path}: unknown scanline filter {filter_type}")

    return width, height, channels, out


def write_png(path, width, height, pixels):
    """Write an 8-bit RGB PNG from a flat bytearray, one filter-0 scanline at a time."""
    stride = width * 3
    raw = bytearray()
    for y in range(height):
        raw.append(0)
        raw += pixels[y * stride:(y + 1) * stride]

    def chunk(kind, body):
        return (struct.pack(">I", len(body)) + kind + body
                + struct.pack(">I", zlib.crc32(kind + body) & 0xFFFFFFFF))

    with open(path, "wb") as handle:
        handle.write(b"\x89PNG\r\n\x1a\n")
        handle.write(chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)))
        handle.write(chunk(b"IDAT", zlib.compress(bytes(raw), 6)))
        handle.write(chunk(b"IEND", b""))


def compare(before_path, after_path, diff_path=None, tolerance=0):
    bw, bh, bc, before = read_png(before_path)
    aw, ah, ac, after = read_png(after_path)
    if (bw, bh) != (aw, ah):
        raise PngError(f"size mismatch: {bw}x{bh} vs {aw}x{ah} — the two captures did not "
                       "use the same --resolution, so they cannot be compared")

    # Compare the channels both images have. Godot writes RGBA for a viewport grab; alpha is
    # constant there, and dropping it keeps a grey/RGB capture comparable with an RGBA one.
    channels = min(bc, ac, 3)
    total = bw * bh
    differing = 0
    worst = 0
    accumulated = 0
    diff = bytearray(total * 3) if diff_path else None

    for index in range(total):
        b_at = index * bc
        a_at = index * ac
        delta = 0
        for c in range(channels):
            d = abs(before[b_at + c] - after[a_at + c])
            if d > delta:
                delta = d
            if diff is not None:
                # Amplify: a delta of 1 is invisible at 1:1 but is exactly what has to be seen.
                diff[index * 3 + c] = min(255, d * 16)
        if delta > tolerance:
            differing += 1
            accumulated += delta
            if delta > worst:
                worst = delta

    if diff is not None:
        write_png(diff_path, bw, bh, diff)

    return {
        "width": bw,
        "height": bh,
        "pixels": total,
        "differing": differing,
        "percent": 100.0 * differing / total if total else 0.0,
        "maxChannelDelta": worst,
        "meanChannelDelta": accumulated / differing if differing else 0.0,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("before")
    parser.add_argument("after")
    parser.add_argument("--diff", help="write an amplified difference image here")
    parser.add_argument("--tolerance", type=int, default=0,
                        help="ignore per-channel deltas at or below this (default 0)")
    parser.add_argument("--max-percent", type=float, default=None,
                        help="exit 1 when more than this share of the frame differs")
    args = parser.parse_args()

    try:
        result = compare(args.before, args.after, args.diff, args.tolerance)
    except (PngError, OSError) as error:
        print(f"compare-screenshots: {error}", file=sys.stderr)
        return 2

    print(f"{args.before} vs {args.after}: {result['width']}x{result['height']}, "
          f"{result['differing']}/{result['pixels']} pixels differ "
          f"({result['percent']:.4f}%), max channel delta {result['maxChannelDelta']}, "
          f"mean {result['meanChannelDelta']:.2f}")
    if args.diff:
        print(f"  difference image: {args.diff}")

    if args.max_percent is not None and result["percent"] > args.max_percent:
        print(f"FAIL: {result['percent']:.4f}% of the frame differs, budget is "
              f"{args.max_percent:.4f}%", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
