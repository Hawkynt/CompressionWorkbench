# DMS reference vectors

These are real DMS archives from Aminet, cut down to their first tracks.
Cutting at a track boundary leaves a valid archive: the header CRC covers only
the header, and each track carries its own CRCs.

| File | Source | Tracks kept | Modes |
| --- | --- | --- | --- |
| `Damage-head.dms` | <https://aminet.net/demo/disk/Damage.dms> (Oldbulls, 1994) | 0–5 | Heavy 2 (with and without new tables, RLE) |
| `Genesis-head.dms` | <https://aminet.net/demo/disk/Genesis.dms> (Austex, ≤1991) | first 14 | stored, Simple RLE, Medium (state carried across NOINIT tracks) |

The SHA-256 values pinned in `DmsExternalConformanceTests.ExpectedSha256` are of
the disk images that `xdms 1.3` (Ubuntu `xdms`) unpacks from these files with
`xdms -q u <file> +<out>`. `DmsExternalConformanceTests` repeats that comparison
live wherever xdms is installed.
