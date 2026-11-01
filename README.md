# SourceKit

Readers for Source engine files, shared by the Compile Pal tools: [Meshwright](https://github.com/catualus/MeshWright),
Viswright and the Compile Pal fork. One copy of each reader, so a fix to how a BSP lump or a VPK is
read reaches every tool at once.

It is a library, not a program. Each tool includes it as a git submodule and publishes it inside its
own self-contained executable.

## What is in it

| Area | Types | Reads |
| --- | --- | --- |
| BSP | `BspFile`, `LzmaLump` | Entities, planes, texinfo, texdata, models, brushes and brush sides, with per-lump LZMA decompression |
| Compile output | `PortalFile`, `PointFile` | VBSP's `.prt` portal file and `.lin` leak pointfile |
| Map source | `VmfDocument`, `VmfBrush`, `VmfDiff`, `VmfRelocation` | A lossless VMF tree, brush geometry built from the plane points, a semantic diff of two maps, and moving a map without losing its instances |
| Game content | `GameFiles`, `GameInfo`, `VpkArchive`, `GmaArchive`, `StudioModel` | Content a map can see, in the engine's search order: its pakfile, loose files, Garry's Mod addons, then VPKs; MDL headers |

Parallel work obeys `SourceKitConcurrency`, which a tool forwards its own `-threads` and Ctrl+C to.

## Rules

- **Read only, unless the type says otherwise.** `VmfDocument` writes a map back out; nothing here ever
  writes over a file it was given - callers choose where output goes.
- **Malformed input is refused, not guessed at.** A count that cannot fit the file or a lump that
  points outside it yields an error or nothing, never garbage.
- **Deterministic.** The same input gives the same result at any thread count.
- **No Valve code.** See [NOTICE.md](NOTICE.md) for what may and may not be taken from the SDK.

## Building

```
dotnet test SourceKit.slnx
```

Licensed GPL-3.0, see [LICENSE](LICENSE).
