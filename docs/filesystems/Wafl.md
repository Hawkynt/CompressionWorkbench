# NetApp WAFL (`Wafl`)

NetApp WAFL — detection-only (Stage-0 confirmed) — proprietary ONTAP filesystem; on-disk tree-of-blocks is partially reverse-engineered from Hitz 1994 + NetApp patents but FBN/VBN/PVBN translation, FlexVol container mapping, RAID-DP block placement, and NVRAM consistency-point gap make a safe single-image R/O reader infeasible from public spec. Magic 'wafd' at offset 0 of FSinfo block.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.wafl` |
| Recognised extensions | `.wafl` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `77 61 66 64` | 0 | 0.90 |

## Verbs

| Verb | Offered | What it does |
|---|---|---|
| list / extract | yes | read the volume and copy files out of it |
| create | no | write a fresh volume holding the given files |
| add / remove | no | change a volume in place |
| defragment | no | lay the volume out again |
| wipe free space | no | zero what no file holds |
| shrink | no | reduce the volume to what it needs |
| optimise layout | no | re-lay the volume at a chosen geometry |
| report layout | no | say where every byte belongs |
| move blocks | no | relocate a run and repoint what names it |
| move metadata | no | relocate the volume's own structures |

### How it defragments

It does not.

## How a volume is laid out

### WaflFormatDescriptor

Stage 0 detection-only descriptor for NetApp WAFL (Write-Anywhere File Layout) volume images. Surfaces only a synthetic `metadata.ini` and the raw image bytes; no real file-walk is attempted.

Stage-0 confirmed. An R/O promotion attempt was investigated against the publicly available material (Hitz 1994 TR3002, NetApp patents WO1994029807 / US6289356, archived ONTAP whitepapers) and declined. The high-level tree-of-blocks design (root inode → inode file → metadata files + user files; 4 KB blocks; FSinfo block at a fixed location anchoring two redundant copies) is published, but the exact byte-level on-disk encoding used by current ONTAP releases is not — neither the inode record layout, the FBN → VBN → PVBN translation tables, the FlexVol container-file mapping, nor the RAID-DP parity scheme used for block addressing have a public spec adequate to extract files from a single-image dump. WAFL is heavily patented and proprietary; no open-source reader exists. The full investigation record is captured in this XML doc, the metadata.ini surface, and the README stub-tier table.

References:

### WaflReader

Stage 0 detection-only reader for NetApp WAFL (Write-Anywhere File Layout) volume images.

WAFL is NetApp's proprietary cluster/NAS filesystem. The on-disk surface for a single file is the FSinfo block that begins each volume label region. The first four bytes of the FSinfo block are the ASCII tag "wafd" (0x77 0x61 0x66 0x64, big-endian as integer 0x77616664), followed by a 32-bit big-endian version field and additional cluster metadata that is not portable outside a NetApp ONTAP controller.

This reader only verifies the magic tag and version field and surfaces the full image as an opaque blob plus a synthetic metadata.ini. No real file-walk is attempted — WAFL's actual directory and inode structures are tightly coupled to ONTAP's volume manager (RAID-DP groups, snapshot trees, FlexVol allocation maps, NVRAM consistency points) and have no published spec sufficient to extract file content from a single-image dump. Sources consulted during the Stage-0 confirmation: Hitz 1994 TR3002 ("File System Design for an NFS File Server Appliance"), NetApp patents WO1994029807 and US6289356, fileformats.archiveteam.org WAFL entry.

## Storage methods

- `stored` — Stored

## Further reading

- Hitz, Lau, Malcolm — "File System Design for an NFS File Server Appliance" (USENIX Winter 1994; NetApp TR-3002), the defining WAFL paper
- NetApp patents WO1994029807 / US6289356 — the published block-layout details
- https://en.wikipedia.org/wiki/Write_Anywhere_File_Layout — Wikipedia article

