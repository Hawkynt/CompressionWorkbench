# UIF reference vectors

The six `.uif` files were written by MagicISO 5.5 build 0281, the unregistered trial of the
official `Setup_MagicISO.exe` (3 067 400 bytes, SHA-256 `8a06368f…c17c1`), installed with
`wine Setup_MagicISO.exe /s` and run under wine 9.0. MagicISO writes UIF only from its window
(Tools > Compress CD/DVD Image), so `make-uif.sh` drives that dialog with xdotool on a virtual X
display. The source images are built in the script from generated files (Python
`random.Random(55)`); `uif-sources.sha256` records the SHA-256 and size of each one.

| File | Made from | Holds |
| --- | --- | --- |
| `plain.uif` | `plain.iso` (an `xorrisofs` image) | version 1, type 8 |
| `password.uif` | `plain.iso`, password `Uif-Secret_31` | `bsdr` block, DES-encrypted table and runs |
| `mixed.uif` | `mixed.cue` + `mixed.bin` (a Mode 1/2352 data track and an audio track) | type 9, source kind 1 (CUE) |
| `mixed-password.uif` | the same, password `Uif-Secret_31` | type 9, encrypted `blms`/`blss` too |
| `clonecd.uif` | `clone.ccd` + `clone.img` + `clone.sub` | type 9, source kind 3 (CloneCD) |
| `nero.uif` | `nero.nrg`, which MagicISO's `miso.exe -conv nrg` made from `mixed.cue` | type 9, source kind 4 (NRG) |

MagicISO's Tools > Decompress UIF Image turns each one back into the files it was made from, byte for
byte. uif2iso 0.1.7c does the same for all but `nero.uif`, where it rewrites parts of the NRG on purpose.
`UifReferenceVectorTests` checks our reader against `uif-sources.sha256`.
