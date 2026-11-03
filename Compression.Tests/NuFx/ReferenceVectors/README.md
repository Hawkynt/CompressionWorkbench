# NuFX reference archives

Two ShrinkIt archives from the CiderPress II test corpus (`TestData/nufx/` in
https://github.com/fadden/CiderPress2, revision `49b67b405a7f82a5b8be369351e381ca72b36796`,
Apache-2.0 repository). They pin `BB_NuLzw` and the NuFX reader against archives other
programs wrote.

| File | Dated | Threads |
|---|---|---|
| `old-archive.shk` | 1996-97 | three LZW/1 data forks: NuLib's `README`, `ChangeLog`, `nulib.doc` |
| `PatchHFS.shk` | 1995 | LZW/2 and stored data forks, one resource fork |

`NuFxCiderPressOracleTests` compares every data fork with the bytes CiderPress II 1.2.0
(`cp2 extract --raw`) wrote for it; the lengths and SHA-256 sums are in the test.
