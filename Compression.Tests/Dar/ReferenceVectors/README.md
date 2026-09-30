# DAR reference slices

Written by `dar` 2.8.6 (the `dar64-2.8.6-win64` build from the dar project on SourceForge), never
by CompressionWorkbench. The source tree was two files under `/tmp/src`: `hello.txt`
(`hello dar\n`) and `noise.bin` (3000 pseudo-random bytes).

| Files | Command | What they pin |
| --- | --- | --- |
| `single.1.dar` | `dar -Q -c single -R /tmp/src` | header flag `T`, TLV list with the data name only, trailer `T` |
| `sliced.1.dar` … `sliced.3.dar` | `dar -Q -c sliced -S 1500 -s 1200 -R /tmp/src` | header flag `E` on every slice, TLVs 2 (first slice size 1500), 1 (slice size 1200), 3 (data name); trailers `N`, `N`, `T` |

`dar -t sliced` accepts the set.
