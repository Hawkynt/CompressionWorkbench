# paq8l reference archives

Archives written by paq8l itself from inputs generated in this repository, so the tests can
check both directions: our reader must decode them to the inputs, and our writer must produce
them byte for byte from the same inputs.

| | |
| --- | --- |
| Tool | paq8l, `paq8l.cpp` from <http://mattmahoney.net/dc/paq8l.zip> (SHA-256 `fb72db122f89faedcc729fe1e3324cac57c9c48903e72107c8350cdbe5e374cf`), built with `g++ -O2 -DUNIX -DNOASM`. Its archives are byte-identical to those of the `paq8l.exe` in the same zip (checked at levels 1, 3 and 5 under wine) |
| Inputs | `make-inputs.py`: generated text, x86-like code with CALL/JMP runs, a 24-bit BMP, a baseline JPEG Pillow made from a generated picture, a fax-width two-level bitmap, binary records, a one-byte and an empty file. No third-party content |
| Recipe | `generate.sh <output dir> [paq8l binary]` |
| `mixed-l0/1/4/5.paq8l` | text, `docs/notes.txt`, code, BMP, JPEG, one-byte and empty file at levels 0, 1, 4 and 5. The level-0 archive shows the blocks: default, EXE (with the E8/E9 transform) and JPEG |
| `fax-l5.paq8l`, `records-l5.paq8l` | the fax bitmap and the records at level 5 |
| `inputs.tar.gz` | the inputs themselves, as `make-inputs.py` wrote them |

`Paq8lOracleTests` runs paq8l live (WSL, `CWB_PAQ8L` or `~/paq8l/paq8l-linux`) on larger
generated sets at levels 1 and 4. These include an uncompressed RGB TIFF and densely packed
calls. paq8l's archive must equal ours, and each side must extract the other's.
