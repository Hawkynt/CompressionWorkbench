# DCS reference vectors

Archives written by DCS 0.10 (Nicholas Clarke, ParCon Software; `dcs010.lha` from amiga-stuff.com,
SHA-256 `1e412c285a7d42e0251faa88f055d48910da43c856a4771da6b32b8c37150337`) on FS-UAE 3.1.66
(A1200, AROS ROM of 2015-05-20) with the XPK user libraries (xpkmaster 5.2), from the floppy
`cwbtest.adf` that the X-Mash vectors come from (SHA-256
`59c11eab0538310e808ccf254ab4c69d8aef25e6d515685e83676d771e9ff5c9`, contents in
`XMashTests.TestFloppyFiles()`). All content is ours.

| File | Command | Contents |
| --- | --- | --- |
| `nuke.dcs` | `DCS Read nuke.dcs CMODE NUKE` | cylinders 0–79, NUKE |
| `rlen.dcs` | `DCS Read rlen.dcs CMODE RLEN` | cylinders 0–79, RLEN |
| `multi.dcs` | `DCS Read multi.dcs CMODE NUKE CMODE SQSH CMODE RLEN` | cylinders 0–79, the shortest stream per cylinder (NUKE or RLEN) |
| `part.dcs` | `DCS Read part.dcs CMODE NUKE LOW 38 HIGH 41` | cylinders 38–41, NUKE |

`docs/DCS-ON-DISK.md` describes the layout.
