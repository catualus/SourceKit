# SourceKit

Readers for Source engine files, shared by the Compile Pal tools: [Meshwright](https://github.com/catualus/MeshWright),
Viswright and the Compile Pal fork. One copy of each reader, so a fix to how a BSP lump or a VPK is
read reaches every tool at once.

It is a library, not a program. Each tool includes it as a git submodule and publishes it inside its
own self-contained executable.

## What is in it

| Area | Types | Reads |
| --- | --- | --- |
| BSP | `BspFile`, `BspSpace`, `BspGeometry`, `BspVisibilityData`, `LzmaLump` | Entities, planes, texinfo, texdata, models, brushes and brush sides; the BSP tree as space (leaf, area and solidity of a point); the world's faces and displacements as triangles; the decoded PVS, leaves, leaf faces, face triangle counts, areas and areaportal numbers; with per-lump LZMA decompression |
| Compile output | `PortalFile`, `PointFile` | VBSP's `.prt` portal file and `.lin` leak pointfile |
| Map source | `VmfDocument`, `VmfBrush`, `VmfDiff`, `VmfRelocation` | A lossless VMF tree that copies without a re-parse (`Clone`) and numbers new blocks without a walk of the map per block (`NextIds`), brush geometry built from the plane points, a semantic diff of two maps, and moving a map without losing its instances |
| Game content | `GameFiles`, `GameInfo`, `VpkArchive`, `GmaArchive`, `StudioModel` | Content a map can see, in the engine's search order: its pakfile (LZMA-repacked ones included), loose files, Garry's Mod addons, then VPKs; MDL headers |

Parallel work obeys `SourceKitConcurrency`, which a tool forwards its own `-threads` and Ctrl+C to.

Every BSP reader, and the pakfile lookup in `GameFiles`, reads the header through one parser. It takes
the `VBSP` ident and versions 19 to 21, and refuses anything else with an "unsupported BSP
version/layout" error - Left 4 Dead 2's version-first lump table included, which would otherwise read
as garbage. `GameFiles` treats a refused map like a damaged pakfile: its packed content is skipped,
nothing else is.

### Where `GameFiles` looks

`GameFiles.Open` works out the install from the map's own path, so the ordinary case needs no
configuration:

- The mod directory is the nearest folder at or above the map holding a `gameinfo.txt`. A map in
  `garrysmod/maps`, `garrysmod/addons/<addon>/maps` or `garrysmod/download/maps` all find `garrysmod`.
  A map in an addon's or `download`'s `maps` folder also searches that folder first, for its own content.
- The mod's `gameinfo.txt` search paths, its `addons`, and Workshop `.gma` files under `cache/workshop`
  and Steam's `workshop/content/4000`.
- Other games Garry's Mod mounts, after all of its own content: folders named in `cfg/mount.cfg`, and
  games ticked in the mount menu (`cfg/mountdepots.txt`), found through Steam's `libraryfolders.vdf` and
  each game's app manifest.
- Folders named with `extraRoots` or `GameFiles.AdditionalRoots`, for a map outside any install. A
  trailing slash on one makes no difference.

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
