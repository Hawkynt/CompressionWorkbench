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

Later runs (45433556177, 45441757023, 45447908924, 45479960118, 45485070018, 45495972996, 45498729400, 45506225287, 45515811153, 45521857931, 45528834750, 45537592424, 45542700359) judged the edits made in place the same way:

| Edit | 3.7 4 KiB | 3.7 64 KiB | 3.7 integrity | 3.14 4 KiB | 3.14 64 KiB | 3.14 integrity |
| --- | --- | --- | --- | --- | --- | --- |
| Overwrite, same length | accepted | accepted | accepted (checksums rewritten) | accepted | accepted | accepted (checksums rewritten) |
| Rename (also to a longer name) | accepted | accepted | accepted | accepted | accepted | accepted |
| Rename to a non-ASCII name | accepted | accepted | not run | accepted | accepted | not run |
| Add a new file | accepted | accepted | accepted (checksums written) | accepted | accepted | accepted (checksums written) |
| Add a directory | accepted | accepted | accepted | accepted | accepted | accepted |
| Remove | accepted | accepted | accepted | accepted | accepted | accepted |
| Replace, longer (new run) | accepted | accepted | refused (entries outgrow the record; Windows moves them to a page of their own) | accepted (leaf grows) | accepted | accepted (leaf grows) |
| Replace, slightly longer (run extended) | accepted | accepted | refused | refused | refused | refused |
| Replace, shorter | accepted | accepted | accepted | accepted | accepted | accepted |
| In a directory over several pages (`many/`): add a file, create a directory, rename, remove | accepted | accepted | accepted | accepted | accepted | accepted |
| In a directory over several pages: grow a file | accepted | accepted | accepted | refused (file kept in its row) | refused (file kept in its row) | accepted |
| In a directory over several pages: add 24 files until a page splits | accepted | accepted | accepted | accepted | accepted (no split needed in a 64 KiB page) | accepted |
| In a one-page directory (`dir1/`): add 120 files, growing it a level | accepted | accepted | accepted | accepted | accepted | accepted |
| In a directory over several pages: remove the three files of one leaf (`f002`–`f004`; on the 4 KiB 3.7 volume this empties a leaf, which leaves the tree) | accepted (run 45656657878) | accepted | accepted | accepted | accepted | accepted |

"Accepted" means Windows mounted the edited volume read-only and read-write, listed every file
with the expected hash, passed `refsutil salvage -QS` and logged no ReFS warning or error.
"Refused" means our editor declined before writing a byte.

## Volumes made from scratch

`RefsVolumeFormatter` writes a new, empty ReFS 3.7 volume (4 KiB clusters, 15 containers =
960 MiB, in a target of up to one container more) from no input at all; its layout is written
down in `docs/REFS-FRESH-VOLUME-ON-DISK.md`. Run 45552136914 judged it like the edits: Windows
Server 2022 mounted the fresh volume read-only and read-write, found it empty, listed a directory
our editor then made in it, passed `refsutil salvage -QS` and logged no ReFS warning or error.
Other sizes, 64 KiB clusters, integrity streams and ReFS 3.14 are refused.

`RefsFormatDescriptor.Create` formats such a volume and adds directories and files to it (a
directory without a file takes the record Windows Server 2022 writes for a new file). Runs
45565257876 to 45607722143 judged a created volume holding nested directories, an empty one and
five files (the largest 200 000 bytes): Windows Server 2022 accepted it at once. Windows Server
2025 mounted it, listed and hashed every file and upgraded it to ReFS 3.14, but then failed
every dismount ("Object Name already exists") until the directory editor kept three tables
Windows keeps: the object table root's highest object number (0x88), the parent-child table
(table 3, one row per directory) and the object table's copy (table 4, identical to it). With
those, run 45607722143 accepted the created volume, and every edit of the matrix above, on all
six kinds.
