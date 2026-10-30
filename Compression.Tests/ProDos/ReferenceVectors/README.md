# ProDOS reference disk (DOS sector order)

| File | Made by | What it is |
|------|---------|------------|
| `cp2-prodos.do.gz` | CiderPress II 1.2.0 (`cp2`, linux-x64 self-contained release, Apache-2.0): `create-disk-image cp2-prodos.do 140k prodos`, then `add` | A 140 KB ProDOS volume "NEWDISK" stored in DOS 3.3 sector order — the container Apple III SOS disks come in — holding a seedling, two saplings and a subdirectory. |
| `cp2-prodos.catalog.txt` | `cp2 catalog` | CiderPress II's own listing of the disk. |
| `manifest.txt` | `generate.sh` | SHA-256 and path of each file added. |

`generate.sh` documents the steps; it is not a build input. Captured 2026-10-30 on
Ubuntu 24.04 under WSL2, compressed with `gzip -9 -n`.

Real Apple III SOS disks (apple3.org) are Apple software and are not checked in;
`ProDosDosOrderTests` compares one against CiderPress II's listing when
`CWB_SOS_DISK` points at it.
