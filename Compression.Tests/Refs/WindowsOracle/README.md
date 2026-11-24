# ReFS volumes Windows formatted

`tools/foreign-os-oracles/refs-capture.ps1` ran in the `Foreign-OS oracles` workflow
(run 45376644436, 2026-11-24) on GitHub-hosted `windows-2022` (Windows Server 2022 Datacenter,
build 20348, ReFS 3.7) and `windows-2025` (Windows Server 2025 Datacenter, build 26100,
ReFS 3.14) runners. Each run formatted three fixed VHDs with ReFS: default (4 KiB clusters),
`-AllocationUnitSize 65536`, and `-SetIntegrityStreams $true`. It copied the same 209 files onto
each volume (the probe's `gen` set) and hashed them with `Get-FileHash`. The 3.7 volumes are
1 GiB and the 3.14 ones 2 GiB, because Server 2025 refused to format 1 GiB.

Each `<runner>-<variant>.json` records:

- the ReFS version `fsutil fsinfo refsinfo` reported;
- the size and SHA-256 of the ReFS partition cut out of the VHD;
- the path, size and SHA-256 of every file;
- what happened to each offline edit the capture tried.

Windows wrote the volumes themselves, so they are not committed. The workflow artifacts
(`refs-windows-2022`, `refs-windows-2025`) hold them, and `RefsWindowsOracleTests` reads them
from `CWB_REFS_WINDOWS_DIR`.

## What Windows said

| | 3.7, 4 KiB | 3.7, 64 KiB | 3.7, integrity | 3.14, 4 KiB | 3.14, 64 KiB | 3.14, integrity |
| --- | --- | --- | --- | --- | --- | --- |
| Our reader, file for file | all 209 | all 209 | all 209 | all 209 | all 209 | all 209 |
| Replace (same size) | applied; Windows: file corrupt | applied; Windows: file corrupt | applied; Windows: file corrupt | refused, image changed | applied; Windows mount failed, files missing | refused, image changed |
| Replace (larger) | applied; Windows: file corrupt | applied; Windows: file corrupt | applied; Windows: file corrupt | refused, image changed | refused, image changed | refused, image changed |
| Remove, rename, clone | refused (Schema Table) | refused | refused | refused | refused | refused |
| Defragment | refused, image changed | refused, image changed | refused | refused | refused | refused |

Windows rejected every image the editor wrote, and the editor sometimes changed an image even
when it refused the edit. That editor is no longer reachable through the descriptor.

A second capture (`refs-windows-edits.ps1`, run 45425176583) had Windows make the same edits
itself. A same-length overwrite of a plain file changes only that file's data clusters in place,
plus timestamps and a new checkpoint. The descriptor now makes exactly that data write and
nothing else. Windows Server 2022 and 2025, at 4 KiB and 64 KiB clusters, mount the result
read-only and read-write, hash the file as written, pass `refsutil salvage -QS` and log no ReFS
error. On the integrity-stream volumes the overwrite is refused and the image is left unchanged.

Later runs (45433556177, 45441757023, 45447908924, 45479960118, 45485070018, 45495972996, 45498729400) judged the edits made in place the same way:

| Edit | 3.7 4 KiB | 3.7 64 KiB | 3.7 integrity | 3.14 4 KiB | 3.14 64 KiB | 3.14 integrity |
| --- | --- | --- | --- | --- | --- | --- |
| Overwrite, same length | accepted | accepted | accepted (checksums rewritten) | accepted | accepted | accepted (checksums rewritten) |
| Rename (also to a longer name) | accepted | accepted | accepted | accepted | accepted | accepted |
| Rename to a non-ASCII name | accepted | accepted | not run | accepted | accepted | not run |
| Add a new file | accepted | accepted | refused (no plain template) | accepted | refused (allocator) | refused (no plain template) |
| Add a directory | refused | refused | refused | refused | refused | refused |
| Remove | accepted | accepted | accepted | accepted | refused (Windows did not mount it) | accepted |
| Replace, longer (new run) | accepted | accepted | refused | refused (leaf full) | refused | refused |
| Replace, slightly longer (run extended) | accepted | accepted | refused | refused | refused | refused |
| Replace, shorter | accepted | accepted | refused | accepted | accepted | refused |

"Accepted" means Windows mounted the edited volume read-only and read-write, listed every file
with the expected hash, passed `refsutil salvage -QS` and logged no ReFS warning or error.
"Refused" means our editor declined before writing a byte.
