# se-cmd

Converts Skyrim's assets to FBX and back: meshes, collision, skeletons, ragdolls
and animations. Drop files on it and it works out what they are.

## Using it

Download the archive from
[Releases](https://github.com/aerisarn-two/se-cmd/releases), unzip it anywhere,
and **drag files or folders onto `se-cmd.exe`**. Everything it produces lands in
an `out` folder beside what you dropped, with a `conversion-log.txt` next to it
saying what each input was taken to be and what came of it.

The same thing from a terminal:

```
se-cmd convert <paths...> [-o out] [-m meshes] [-t skeleton.hkx]
se-cmd <paths...>              # the verb is optional
```

| what you drop | what you get |
| --- | --- |
| a creature's folder | one FBX: its skeleton, its bodies, its ragdoll and every animation its project has |
| a `.nif` | that mesh as an FBX, with its collision |
| a `skeleton.hkx` | its rig and ragdoll as an FBX |
| an animation `.hkx` | the clip over the rig found above it |
| an `.fbx` | back into the files the game reads — `.nif`, `skeleton.hkx`, animation packfiles and the cache entries that address them |
| a folder of anything | each creature whole, then every other file it holds |

Nothing is guessed at. Each file is opened and asked what it is, because the
extension does not say: a `.nif` may be a rock, a windmill, a skinned body or an
actor's skeleton, and a `.hkx` may be a rig or a clip.

### What it needs to do the whole job

Two things are found automatically when they are there, and named when they are
not:

- **`-m`, the extracted `meshes` folder** holding `animationdatasinglefile.txt`.
  A creature's travel — how far a walk carries it — is in that cache and not in
  the animation, so without it a creature's clips are skipped. It is looked for
  above whatever you dropped.
- **`-t`, a `skeleton.hkx` to write the Havok half into.** A Havok packfile is a
  thousand objects of scaffolding, so one is edited from an original rather than
  built from nothing. Re-importing a creature you exported, that is its own
  `skeleton.hkx`, found above the FBX.

Where either is missing the log says so and that part is skipped rather than
half-written.

### What a converted file says about itself

The NIF header's `Process Script` records the tool and the library — `se-cmd
0.1.10.0, NIFBX 1.6.1` — and the FBX's `Creator` says the same. `Author` is left
alone: that is where a NIF keeps the name of whoever made it, and 1,046 of the
game's 1,106 meshes use it.

## The other commands

`convert` covers the conversion, in both directions, for anything it is handed:
a NIF, an HKX, an FBX, or a creature's folder. It asks the file what it is, so
there is nothing for a command naming the direction to do. These are the jobs
that are not conversions:

| | |
| --- | --- |
| `findnpc` | say which files a creature is made of, without converting any of them |
| `exportnpc` | gather the files a record configures, out of the archives, into one FBX |
| `retarget` | a creature's Havok project, forms and assets onto another actor |

## Where the work is

Almost none of it is here. This is the command line; the conversion lives in its
own repositories and arrives as a package:

| | |
| --- | --- |
| [NIFBX](https://github.com/aerisarn-two/NIFBX) | NIF ↔ FBX, and the specs and corpus tests behind it |
| [SKAssets](https://github.com/aerisarn-two/SKAssets) | what an asset is made of, and a creature as one scene |
| [HKSK](https://github.com/aerisarn-two/HKSK) | the Havok animation cache |
| [HKFBX](https://github.com/aerisarn-two/HKFBX) | Havok rigs and clips ↔ FBX |
| [NIFSharp](https://github.com/aerisarn-two/NIFSharp) | reading and writing the NIF itself |
| [LeanMeshIO](https://github.com/aerisarn-two/LeanMeshIO) | the FBX as a raw node tree |

A change to the conversion belongs in one of those. This repository is the
command line over them.

## Building

```
dotnet build
```

The packages come from GitHub Packages, so a restore needs `GITHUB_USERNAME` and
a `GITHUB_TOKEN` carrying `read:packages`; `nuget.config` takes them from the
environment.

## Licence

GPL-3.0-or-later.
