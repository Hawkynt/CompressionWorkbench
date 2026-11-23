# X-Mash reference vectors

Archives written by X-Mash 1.2 (Hydra/LSD, Aminet `util/pack/lsdxms12.lha`, with the
XPK libraries from that package) from one test floppy, `cwbtest.adf`, made with our `AdfWriter`.
The floppy is not committed; its SHA-256 is pinned in `XMashTests`
(`59c11eab0538310e808ccf254ab4c69d8aef25e6d515685e83676d771e9ff5c9`). It holds an FFS volume
`CWBTEST` with `readme.txt` (300 numbered lines), `table.bin` (8 KiB pattern), `noise.bin`
(3,000 xorshift bytes) and `zeros.bin` (5,000 zeros); `XMashTests.TestFloppyFiles()` regenerates
their contents.

| File | Command | Contents |
| --- | --- | --- |
| `full-nuke.xms` | `XMash read full-nuke.xms from DF0: mode NUKE` | tracks 0–159, NUKE |
| `full-sqsh.xms` | `XMash read full-sqsh.xms from DF0: mode SQSH` | tracks 0–159, SQSH |
| `banner-rlen.xms` | `XMash read banner-rlen.xms from DF0: mode RLEN text banner` | banner block, then tracks 0–159, RLEN |
| `part-nuke.xms` | `XMash read part-nuke.xms from DF0: low 38 high 41 mode NUKE` | tracks 76–83 (cylinders 38–41), NUKE |

The banner file is the 28 bytes `CWB test banner\nsecond line\n`; X-Mash stores it with a
terminating NUL (29 bytes).
