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

## Reproducibility

The archives were written by X-Mash 1.2 on FS-UAE 3.1.66 (A1200, AROS ROM of 2015-05-20) and
regenerated later on the Linux build of the same FS-UAE version and the same ROM build, with the
ROM's memory-debugging option switched off. Which of these differences changes the SQSH and
banner output was not isolated.

| File | Regenerated | Note |
| --- | --- | --- |
| `full-nuke.xms` | byte for byte | |
| `part-nuke.xms` | byte for byte | |
| `full-sqsh.xms` | no | every SQSH stream of the regenerated archive is 4 bytes longer (53,683 bytes in all) |
| `banner-rlen.xms` | no | only the banner's XPK stream differs: here its one chunk is RLEN-packed (31 bytes for 29), in the regenerated archive it is stored |

Both archives that did not reproduce are kept: each is genuine X-Mash 1.2 output, and each
rebuilds the source floppy byte for byte (SHA-256 above) with our reader, as do the regenerated
ones. The regenerated `banner-rlen.xms` also carries the same 29-byte banner.
