# DCS (Amiga Device Compression System) on disk

DCS 0.10 (Nicholas Clarke, ParCon Software, 1994–95, `dcs010.lha` on amiga-stuff.com, SHA-256
`1e412c285a7d42e0251faa88f055d48910da43c856a4771da6b32b8c37150337`) packs an 880 KiB floppy into one
file, cylinder by cylinder, with the XPK libraries. Its own manual is six lines long:

```
DCS Read File [CMODE XPKLIB[.0-100]] [ENCRYPT XPKLIB[.0-100] PSW]
DCS Write File [DECRYPT PSW]
```

`Read` packs the disk in DF0: into `File`; `Write` unpacks `File` back onto DF0:. `LOW`/`HIGH`
select a cylinder range. Without `CMODE` the method is `NONE`; with several `CMODE`s every cylinder
is packed by each of them and the shortest stream is kept.

Sources: libxad's DCS client (XADMaster `libxad/clients/DCS.c`, LGPL-2.1-or-later, sourcing rung 1)
for the layout, and the measurements below, made by running DCS 0.10 on FS-UAE 3.1.66 (A1200, AROS
ROM of 2015-05-20) with the XPK user libraries (`xpk_User`, xpkmaster 5.2) against `cwbtest.adf`, the test floppy of the X-Mash
vectors (SHA-256 `59c11eab0538310e808ccf254ab4c69d8aef25e6d515685e83676d771e9ff5c9`).

## Layout

All numbers are big-endian.

| Offset | Size | Field |
| ---: | ---: | --- |
| 0 | 3 | `DCS` |
| 3 | 2 | zero |
| 5 | 1 | number of encryption passes (0 = plain) |
| 6 | … | cylinder records |

Cylinder record:

| Offset | Size | Field |
| ---: | ---: | --- |
| 0 | 2 | `TR` |
| 2 | 1 | zero |
| 3 | 1 | cylinder number |
| 4 | 2 | length `n` of the XPK stream |
| 6 | n | one XPK stream (`XPKF`), unpacking to 11,264 bytes (2 heads × 11 sectors × 512) |

The records follow each other without gaps or padding, in ascending cylinder order with no
cylinder missing, and the file ends with the last record. There is no trailer, table or checksum
beyond the ones inside each XPK stream. A cylinder outside the recorded range is not stored; a
reader rebuilding the whole floppy leaves it zero.

With `ENCRYPT`, libxad expects one XPK layer per pass on top of the packing layer, each encrypted
with its own password (passwords separated by `|`). No encrypted archive was made; this reader
refuses them.

## Measured

| Archive | Command | Size | Records | Streams |
| --- | --- | ---: | --- | --- |
| `nuke.dcs` | `DCS Read nuke.dcs CMODE NUKE` | 40,394 | cylinders 0–79 | NUKE |
| `rlen.dcs` | `DCS Read rlen.dcs CMODE RLEN` | 47,086 | cylinders 0–79 | RLEN |
| `multi.dcs` | `DCS Read multi.dcs CMODE NUKE CMODE SQSH CMODE RLEN` | 24,378 | cylinders 0–79 | NUKE and RLEN |
| `part.dcs` | `DCS Read part.dcs CMODE NUKE LOW 38 HIGH 41` | 3,186 | cylinders 38–41 | NUKE |
| `none.dcs` | `DCS Read none.dcs` | 905,766 | cylinders 0–79 | NONE |

- Every archive starts `44 43 53 00 00 00`: no encryption pass.
- Every record is `TR`, zero, the cylinder, the length; every stream says it unpacks to 11,264 bytes.
- `LOW 38 HIGH 41` counts cylinders, not tracks: the archive holds the four cylinders 38–41.
- In `multi.dcs` each of the 80 records equals, byte for byte in length and sub-library, the shorter
  of the NUKE and RLEN records of the same cylinder in `nuke.dcs` and `rlen.dcs`; no SQSH stream was
  kept, as SQSH never came out shortest on this floppy.
- `none.dcs` holds 80 records of 11,316 bytes: a 36-byte XPK header, one stored chunk of 8 + 11,264
  bytes and the 8-byte end chunk.

Every full archive rebuilds `cwbtest.adf` byte for byte; `part.dcs` rebuilds cylinders 38–41 of it.
