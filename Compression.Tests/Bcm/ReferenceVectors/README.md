# BCM reference vectors

Files written by Ilya Muravyov's bcm. They are the test data for `BcmReferenceTests`.

| | |
|---|---|
| Written by | bcm 1.65 (`src/bcm.cpp` of `encode84/bcm` at `37d7398b49f85defa1f2a10e13ed5aff6fb9608e`, as archived by Software Heritage), built with MinGW-w64 GCC: `g++ -O2 bcm.cpp libsais.c` |
| Cross-checked | the official Windows x64 binaries of bcm 1.60 (`bcm160_win64.zip`, `bcm.exe` SHA-256 `b5cb02c7…629a1b`) and bcm 1.10 beta (`bcm110beta_win64.zip`, `bcm.exe` SHA-256 `b31a00bb…7ae6d6`) write byte-identical files for every default-option case |
| Inputs | deterministic generators (random words, repeated lines, noise), mirrored in the test class and in `make-fixtures.py`; only their size and SHA-256 are kept |
| Checked | bcm 1.65 `-d` returned each input before the file was saved |

`manifest.tsv` lists file, decoded size, SHA-256 of the decoded bytes, the bcm options and the file size.
Regenerate with `python make-fixtures.py <bcm 1.65> [<other 1.x builds>] --v2 <bcm 2.03>`.

| File | What it covers |
|---|---|
| `text-20k.bcm`, `text-100k.bcm` | one block |
| `repeats-2500k-b1.bcm` | three 1 MiB blocks (`-b1`) |
| `noise-3000.bcm` | incompressible input (BCM has no stored mode) |
| `tiny-hello.bcm`, `one-byte.bcm`, `empty.bcm` | the smallest inputs; an empty file is the end marker and CRC only |
| `bcm2-text-5000.bcm` | written by BCM 2.03 (`bcm203.zip` from compressme.net, `bcm.exe` SHA-256 `0d92aff0…4722a9b`); a different, unpublished format (`BCM2`) that the reader refuses. BCM 1.65 refuses it too, and BCM 2.03 refuses 1.x files. |
