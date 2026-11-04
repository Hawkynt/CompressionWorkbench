# CRUNCH reference files

Three crunched files from the Walnut Creek CP/M CD (mirrored at
http://www.classiccmp.org/cpmarchives/cpm/Software/WalnutCD/, freely distributed CP/M user-group
software). lbrate 1.1 (Russell Marks, GPL-2.0, built with gcc 16.2 and run as a black box)
extracted the bytes the test expects; their lengths and SHA-256 sums are in the test.

| File | Path on the CD | Stream | Unpacked |
|---|---|---|---|
| `zsystem.izs` | `beehive/zcat/zsystem.izs` | CRUNCH 1.x (version `0x10`) | `ZSYSTEM.INS`, 2304 bytes |
| `mxform.bzs` | `lambda/soundpot/f/mxform.bzs` | CRUNCH 2.x (`0x20`) | `MX80FORM.BAS`, 7424 bytes |
| `delbr.czm` | `lambda/soundpot/a/delbr.czm` | CRUNCH 2.x (`0x20`), more codes than one table | `DELBR.COM`, 13184 bytes |
