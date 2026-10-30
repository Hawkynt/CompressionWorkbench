# UNIX V6 reference volume

| File | Source | What it is |
|------|--------|------------|
| `v6root.gz` | The Unix Heritage Society archive, `Distributions/Research/Dennis_v6/v6root.gz`, byte-identical (SHA-256 `489bcda3a690dd445e670f4ce521520149857536ae4eb146d9d8a419401f4a66`) | The Sixth Edition root file system (RK05, 4000 blocks) from Dennis Ritchie's tapes, 1975. |
| `manifest.txt` | Generated from `Dennis_v6/v6root.tar.gz` in the same archive (SHA-256 `d20065d6ebf464bd357205022b46924d58e56a7504f5b694f29246a74d6b28c6`), an independent extraction of the same volume | `F sha256 size mtime path` for each of the 289 files, `D mtime path` for each of the 23 directories. Times are seconds since the epoch, UTC. |

The manifest was made with Python's `tarfile` on 2026-10-26; the tar records no
device nodes, and the reader does not list them either.

The software in `v6root.gz` is distributed under the Caldera Ancient UNIX licence
of 23 January 2002, reproduced in `LICENSE.Caldera.txt`:

Copyright(C) Caldera International Inc. 2001-2002. All rights reserved.
This product includes software developed or owned by Caldera International, Inc.
