# GZA / BZA reference vectors

The oracle is the original BGA32.DLL 0.37 (Toshinobu Kimura, 2002), from `bga32037.lzh` at
<https://www.madobe.net/archiver/lib/bga32.html>. It is driven through `Bga()` by a small x86
harness compiled with the .NET Framework `csc`.

## Written by BGA32.DLL

`bga-gzip.gza` is `a -r -i -m1`, and `bga-bzip2.bza` is `a -r -i -m2`, over this tree:

| Item | Content | SHA-256 |
| --- | --- | --- |
| `docs/notes.txt` | 500 generated CRLF lines (16 845 bytes) | `b1c7fc11…bef09` |
| `docs/sub/tiny.txt` | `tiny` | `8950abfd…e10c` |
| `noise.bin` | 6 000 LCG bytes | `5aa448bd…fd24d` |
| `already.zip` | an empty ZIP (22 bytes) | `8739c76e…6d85` |
| `empty.txt` | empty | |

BGA32 compresses `notes.txt` and stores everything that would not shrink. Directory records follow
their contents, and timestamps are DOS local time.

## Ours, read by BGA32.DLL

`ours.gza` and `ours.bza` are `BgaTests.WriteOurs`. `ours-edited.bza` is `bga-bzip2.bza` after our
`Remove("noise.bin")` and `Add("added.txt")`. BGA32's `l` lists all three, and its `x` extracts
every file with the expected SHA-256 (`bga32-listing.txt`).
