# PackDisk reference vectors

Archives written by PackDisk 1.1 (Edgar M. Vigdal, Aminet `util/arc/PackDisk11.lha`, with the XPK
libraries of Aminet `util/pack/xpk_User.lha`) from one test floppy made with our `AdfWriter`: an FFS
volume `CWBTEST` with four files, SHA-256
`59c11eab0538310e808ccf254ab4c69d8aef25e6d515685e83676d771e9ff5c9` (the same floppy as the X-Mash and
PackDev vectors). The floppy is not committed.

| File | Command | Cylinders |
| --- | --- | --- |
| `mode1.pdsk` | `PackDisk -p -1 mode1` | 80 × RLEN |
| `mode2.pdsk` | `PackDisk -p -2 mode2` | 77 × RLEN, 3 × NUKE (PackDisk keeps the smaller of the two per cylinder) |

PackDisk wrote both names with a trailing space; the `.pdsk` extension was added here.
