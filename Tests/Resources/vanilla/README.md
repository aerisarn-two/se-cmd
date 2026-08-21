# Divergent vanilla meshes

`divergent.txt` lists the meshes that do not survive the FBX round trip byte for
byte, as found by `BsaCorpusTests.EveryVanillaMeshSurvivesTheFbxRoundTrip`. It is
a list of paths, which is all that is committed here: the meshes themselves are
Bethesda's and are **git-ignored**.

Populate the folder once, from an installed copy:

```sh
SECMD_SKYRIM_DATA="/path/to/Skyrim Special Edition/Data" \
    dotnet test --filter "FullyQualifiedName~ExtractsTheDivergentMeshes"
```

After that the analysis needs no game data and no sweep:

```sh
dotnet test --filter "FullyQualifiedName~DivergentCorpus"
```

The point is the difference in cost. The sweep is 22,047 meshes and about
seventeen minutes; this is a dozen or so and about a second, which is the
difference between checking a hypothesis and deciding not to bother.

The meshes land in the build output beside the other fixtures, not here, so a
`clean` discards them and the extraction is run again. That is the right way
round: they are derived from something you already have rather than part of the
repository.

Re-run the extraction after updating `divergent.txt` — the sweep writes the same
list to whatever `SECMD_FAILURES` names, first column:

```sh
cut -f1 failures.tsv | tr '\\' '/' | sort > Tests/Resources/vanilla/divergent.txt
```
