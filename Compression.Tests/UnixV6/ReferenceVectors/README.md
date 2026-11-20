# UNIX V6 reference volume

| File | Source | What it is |
|------|--------|------------|
| `v6root.gz` | The Unix Heritage Society archive, `Distributions/Research/Dennis_v6/v6root.gz`, byte-identical (SHA-256 `489bcda3a690dd445e670f4ce521520149857536ae4eb146d9d8a419401f4a66`) | The Sixth Edition root file system (RK05, 4000 blocks) from Dennis Ritchie's tapes, 1975. |
| `manifest.txt` | Generated from `Dennis_v6/v6root.tar.gz` in the same archive (SHA-256 `d20065d6ebf464bd357205022b46924d58e56a7504f5b694f29246a74d6b28c6`), an independent extraction of the same volume | `F sha256 size mtime path` for each of the 289 files, `D mtime path` for each of the 23 directories. Times are seconds since the epoch, UTC. |

The manifest was made with Python's `tarfile` on 2026-10-26; the tar records no
device nodes, and the reader does not list them either.

## Checking written volumes

`v6check.py` boots UNIX V6 on SIMH's PDP-11 simulator (`pdp11`, SIMH 3.8) from the root
volume of SIMH's V6 kit (`uv6swre.zip`, `unix0_v6_rk.dsk`; same Caldera licence), with a
written volume attached as RK05 drive 4. V6 then runs `icheck` and `dcheck` on it, mounts
it, lists it, copies files inside it, unmounts it and checks it again. The kit is not
committed; `UnixV6WriterTests` looks for it in `$CWB_V6_KIT`, else `~/v6kit`, inside WSL.

The software in `v6root.gz` is distributed under the Caldera Ancient UNIX licence
of 23 January 2002, reproduced in `LICENSE.Caldera.txt`:

Copyright(C) Caldera International Inc. 2001-2002. All rights reserved.
This product includes software developed or owned by Caldera International, Inc.
