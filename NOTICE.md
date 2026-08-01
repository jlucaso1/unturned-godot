# Notice and attribution

## Not affiliated with Smartly Dressed Games

This is an independent, unofficial hobby project. It is **not** made, endorsed,
sponsored or supported by [Smartly Dressed Games](https://smartlydressedgames.com/)
or Nelson Sexton, and it is **not** a release of Unturned. "Unturned" and all
related names, logos and content are the property of Smartly Dressed Games.

## No game content is redistributed

The repository contains source code only. Every map, mesh, texture, material and
sound this project renders is read at runtime from **your own** installation of
Unturned, obtained through Steam. Nothing is copied into the repository, and no
build artifact of this project embeds game assets.

Without Unturned installed the project builds and its test suite passes, but
there is no world to load.

## What the file format work is based on

The binary and text formats (heightmaps, splatmaps, `Level/Objects.dat`, the
`.dat` grammar, navmeshes, asset definitions, …) were re-implemented in C# by
reading the game's own published sources and documentation:

- [U3-SDK](https://github.com/SmartlyDressedGames/U3-SDK): Smartly Dressed Games'
  official Unturned SDK, whose scripts define the serialization used here. It is the
  reference this port was written against.
- [Unturned documentation](https://docs.smartlydressedgames.com/): asset and
  master bundle documentation.

The implementations in `core/` are original code written for this project; they
are not copies of SDK source files. Where a parser mirrors a specific SDK type,
the corresponding class is named in a comment at the top of the file so the
behaviour can be checked against the reference.

## Ported algorithms

`core/Unity/CrunchCodec.cs` and `core/Unity/CrunchTexture.cs` decode the Crunch
(`.crn`) container Unity's "crunched" texture compression produces. They are a C#
port of the reference decoder in [crunch](https://github.com/BinomialLLC/crunch)
by Binomial LLC / Rich Geldreich (`crn_decomp.h`, which its author placed in the
public domain; the project is distributed under the zlib licence). The bit layout,
the canonical-code tables and the chunk encodings have to match it exactly, so the
port follows its structure; no source file from it is included here.

## Third-party dependencies

| Dependency | Used for | License |
|---|---|---|
| [Godot Engine](https://godotengine.org/) (.NET) | Engine and runtime | MIT |
| [SharpCompress](https://github.com/adamhathcock/sharpcompress) | LZMA decode of the master bundle block | MIT |
| [Fmod5Sharp](https://github.com/SamboyCoding/Fmod5Sharp) | FSB5 audio decode | MIT |
| [xUnit](https://xunit.net/) | Test suite | Apache-2.0 |

If you are a rights holder and want something here changed or removed, please
open an issue.
