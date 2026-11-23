# PackDev reference vectors

Images written by PackDev 1.9 (Christian Wasner, Aminet `util/arc/PackDev1_9.lha`, with the XPK
libraries of Aminet `util/pack/xpk_User.lha`) from one test floppy made with our `AdfWriter`: an
FFS volume `CWBTEST` with four files, SHA-256
`59c11eab0538310e808ccf254ab4c69d8aef25e6d515685e83676d771e9ff5c9` (the same floppy as the X-Mash
vectors). The floppy is not committed; `rlenall.pkd` holds every block of it.

| File | Command | Contents |
| --- | --- | --- |
| `stored.pkd` | `PackDev DF0: stored.pkd NC` | free-block map, used blocks stored with their labels |
| `nuke.pkd` | `PackDev DF0: nuke.pkd P NUKE NC` | free-block map, one NUKE buffer |
| `sqsh.pkd` | `PackDev DF0: sqsh.pkd P SQSH NC` | free-block map, one SQSH buffer |
| `rlenall.pkd` | `PackDev DF0: rlenall.pkd P RLEN ALL NC` | every block, several RLEN buffers |
