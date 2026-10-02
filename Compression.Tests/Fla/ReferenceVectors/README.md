# FLA reference vectors

Saved by Adobe Flash Professional CS6 (`creatorInfo="Adobe Flash Professional CS6"`, XFL 2.2,
builds 481/537 per `META-INF/metadata.xml`), never by CompressionWorkbench. Taken unmodified from
the JPEXS Free Flash Decompiler test data (`libsrc/ffdec_lib/testdata/` in
https://github.com/jindrapetrik/jpexs-decompiler, commit `de7efbf4754865f46c1c5bd36c9db9cc4ef2fa0b`,
fetched 2026-10-01).

| File | Upstream path | Bytes | What it covers |
| --- | --- | --- | --- |
| `run_as2.fla` | `run_as2/run_as2.fla` | 6256 | one library symbol |
| `slash_syntax.fla` | `as2_slash_syntax/slash_syntax.fla` | 6692 | two symbols, members added after the first save |
| `namespaces.fla` | `namespaces/namespaces.fla` | 4432 | empty library (only the `LIBRARY/` folder entry) |
| `test_images/` | `gfx/test_images/` | 10 files | uncompressed XFL folder with two bitmaps and their `bin/*.dat` data |

`ffdec_lib` is licensed under the GNU Lesser General Public License, version 3
(https://www.gnu.org/licenses/lgpl-3.0.html), as is this repository.

What the files show about a compressed FLA, all three alike: the members are the zipped XFL folder
(`<name>.xfl` holding `PROXY-CS5`, `LIBRARY/` and `META-INF/` folder entries, deflated XML,
stored empty or tiny files), the 25-byte `mimetype` member (`application/vnd.adobe.xfl`) is stored
and comes **last**, and the end-of-central-directory record overstates the central directory's size
by 54 bytes (Info-ZIP warns about it, Python's `zipfile` refuses it, xfl2svg corrects it).

## Captured reports

`*.xfl2svg.txt` are what xfl2svg (https://github.com/PluieElectrique/xfl2svg, MIT, commit
`33304eff3c06be6e4a0637b7dbee04b3e37a47b9`) made of each vector under CPython 3.13 on Windows,
produced by the report script in `../FlaXfl2SvgOracleTests.cs`: the `mimetype` member's position and
method, every member's size and SHA-256 as Python's `zipfile` read it, the stage, every scene and
symbol with its frame count and the hash of its rendered SVG frames, and each media item's data.
The core tests compare our reader with the member lines; the oracle tests ask xfl2svg live.

To run the oracle tests, install xfl2svg into the Python on the PATH:

    python -m pip install flit_core
    python -m pip install --no-build-isolation "xfl2svg @ https://codeload.github.com/PluieElectrique/xfl2svg/zip/33304eff3c06be6e4a0637b7dbee04b3e37a47b9"
