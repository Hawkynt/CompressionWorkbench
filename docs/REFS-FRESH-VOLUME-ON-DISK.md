# ReFS 3.7: a freshly formatted volume, on disk

This is what `RefsVolumeFormatter` writes, and where each value comes from. Everything here was
measured on volumes that Windows Server 2022 (ReFS 3.7) formatted on a GitHub-hosted runner.
The volumes were captured by the `Foreign-OS oracles` workflow (`refs-windows-edits.ps1`, the
`empty` variants, run 45544775573): formatted with `Format-Volume -FileSystem ReFS`, mounted
read-write, dismounted, and copied out of the VHD.

The measurements were made by reading the volume with this repository's ReFS reader and by
comparing clusters. Nothing was taken from Microsoft code. The captured volumes are not committed.
`RefsVolumeFormatterTests` compares the formatter's output with them row by row wherever
`CWB_REFS_EMPTY_DIR` points at the capture.

Proof: run 45552136914. Windows Server 2022 mounted a volume this formatter wrote, read-only and
read-write. It listed a directory made in it afterwards, `refsutil salvage -QS` found nothing
wrong, and no ReFS warning or error was logged.

## Geometry made

| Field | Value |
| --- | --- |
| Version | 3.7 |
| Cluster | 4096 bytes |
| Metadata page | 16 KiB (4 clusters) |
| Clusters per container | 0x4000 (64 MiB) |
| Volume | 15 containers, 0x3C000 clusters (960 MiB) |

The target may be up to one container larger, as the 1023 MiB partition of a 1 GiB VHD is.
Windows leaves the rest unused, and so does the formatter. Other sizes, 64 KiB clusters, integrity
streams and ReFS 3.14 are refused.

## Identity

- **Page signature.** Every MSB+ page, superblock, checkpoint and log block stores a u32 at +0x04
  (log blocks) or +0x0C (pages). Its value is the XOR of the volume GUID's four dwords. Example:
  GUID `9BB8118C E5DC3E49 89B82F01 000E4265` gives `0xA142D2F7`. A random signature that is not
  derived this way makes Windows fail the mount with "volume repair was not successful".
- **Volume GUID.** Superblock +0x50 (16 bytes).
- **Log GUID.** Log block +0x10, and again in the control payload at +0xD8.
- **Serial number.** Boot sector +0x38 (u64).
- **Timestamps.** Every directory and file record carries four FILETIMEs at value +0x28..+0x48.
  The volume information's `0x520` row carries the format time at +0x90 and the last mount time
  at +0xA0. The formatter writes the format time to all of them.

## Fixed structures

| Cluster | Content |
| --- | --- |
| 0 | Boot sector: `ReFS` at 3, `FSRS` at 0x10, length 0x200, checksum at 0x16 (rotate-right-and-add over bytes 3..511, the field itself skipped), total sectors 0x18, bytes per sector 0x20, sectors per cluster 0x24, version 3.7 at 0x28, flags 0x26 at 0x2C, serial at 0x38, bytes per container at 0x40. A copy sits in the volume's last sector. |
| 0x1E, 0x3BFFD, 0x3BFFE | Superblocks: generation 1 at 0x68; checkpoint list offset 0xC0 and count 2 at 0x70; self-checksum descriptor at 0xD0, length 0x68, with the superblock's own LCN at 0xD0 and a CRC32 (type 1, 4 bytes) at 0xF8. |
| 0x9BC, 0x7350 | Checkpoints: virtual clock 0x0E and 0x0F (+0x10, +0x60); allocator clock 0x0A and 0x0B (+0x68); oldest required log LSN 0x10000000E (+0x70); flags 2 (+0x78); 0x680 and 0x20 at +0x88/+0x8C; 13 roots (+0x90); root descriptor offsets 0x138 + 0x68·i (+0x94). Each descriptor is a 0x30-byte page reference. The self checksum is laid out as in the superblock. |
| 0xC000..0x1C000 | Log data area: zero on a fresh volume. |
| 0x1C000, 0x1C001 | Log control blocks (see below); 0x1C000..0x1C003 are allocated. |
| 0..0x23 | Boot area, allocated as a whole. The last four clusters (superblock copies, boot sector copy) are allocated too. |

## Containers

Virtual address = (container << 15) | offset; the offset is below 0x4000.

| Container | Physical base | Class at +0x14 |
| --- | --- | --- |
| 2, 3 | 0x0, 0x4000 | 1 |
| 4 | 0x38000 (the last band) | 1 |
| 5..9 | 0x8000..0x18000 | 0x4001 |
| 0xA | 0x1C000 | 1 |
| 0xB, 0xC | 0x20000, 0x24000 | 0x4200 |
| 0xD | 0x28000 | 0x2040 |
| 0xE..0x10 | 0x2C000..0x34000 | 0x2000 |

A Container Table row (roots 7 and 8, physically addressed) is 0xA0 bytes, key inside value:

- `[container u64, u32 0, u32 1]`;
- u32 1 at +0x10, the class at +0x14, clusters per container at +0x18;
- 16 bytes 0xFF at +0x20;
- u32 1 at +0x4C;
- the physical base at +0x90 and the length at +0x98.

## MSB+ pages

- **Page header.** `MSB+` at 0, u32 2 at 4, the signature at 0x0C, a clock at 0x10, a per-table
  count at 0x18, four self addresses at 0x20 (virtual, except for the Container Tables and the
  Small Allocator), the table id at 0x48, and the node offset (relative to 0x50) at 0x50.
- **Table descriptor (root pages) at 0x54.** u16 0x28, u16 kind, u32 1, the schema id twice,
  u16 2, u16 extra, the leaf-page count (u64) at 0x68 and the table's row count (u64) at 0x70.
  The table header follows, up to the node.
- **Leaf pages below a root.** Node offset 8, node at 0x58.
- **Node.** `[0x28, data end, free, flags, index start, count, 0, 0, index end]`. Flags are 0x200
  for a root leaf, 0x600 for allocator-like roots, 0x301 for an inner root and 0 for a leaf below
  a root. The index holds `offset | 0xFFFF0000` per row.
- **Row.** `[size, key offset 0x10, key length, flags, value offset, value length]`. Some rows store
  their key inside their value (value offset 0x10): allocators, Container Table, parent-child
  table, table F.

| Table | Page | Node | Schema | Kind/extra | Clock/0x18 | Rows |
| --- | --- | --- | --- | --- | --- | --- |
| 0x22 Small Allocator (root 12) | 0x24 (physical) | 0x278 | 0xE010 | 0xE010 | 1/0 | [0x24, 0xC] short bitmap (padding bits set), [0x8000, 0x4000] |
| 0xB/0xC Container Table (roots 7, 8) | 0x28/0x2C (physical) | 0x278 | 0xE0C0 | 0 | 1/0 | 15 containers; header 0x10 at 0x78, 0x7FFFFFFFFF00 at 0x80, 0x4000 at 0x88, 15 at 0xA0 |
| 0x530 security descriptors | 0x30 | 0x138 | 0x140 | 1 | 0xB/0 | three default descriptors |
| 0x500/0x501 volume information | 0x34/0x2404 | 0x138 | 0x150 | 1 | 0xA/0 | label (0x510), version and times (0x520), 0x23/1 (0x540) |
| 0x520 metadata directory | 0x1A04 | 0xB8 | 0x130 | 1/1 | 8/6 | header and the volume-direct-IO, security-descriptor and reparse-index records |
| 0x701 System Volume Information | 0x200C | 0xB8 | 0x130 | 1/0 | 0xB/6 | header (hidden, system, its own descriptor) |
| 0x3 parent-child (root 4) | 0x2200 | 0x278 | 0xE040 | 0 | 0xC/0 | root → 0x520, root → 0x701 |
| 0x541/0x540 | 0x2400/0x3400 | 0x138 | 0x160 | 1 | 8/4 | empty |
| 0x600 root directory | 0x2604 | 0xB8 | 0x130 | 1/0 | 0xB/6 | header (name ".") and the System Volume Information name row |
| 0x2/0x4 object table (roots 0, 5) | 0x2A04/0x3200 | 0x278 | 0xE030 | 0 | 0xC/0 | 13 objects; 1 at 0x78, highest object 0x701 at 0x88 |
| 0xE (root 10) | 0x1C004 | 0x278 | 0xE100 | 0 | 1/0 | empty |
| 0x1/0x6 schema (roots 3, 9) | 0x1C014/0x1C018 inner, 0x1C080/0x1C084 leaf | 0x140 (index end 0xCA8) | 0xE060 | 0 | 1/0 | 30 schema definitions |
| 0x8/0x7 upcase (objects 8, 7) | 0x1C01C/0x1C020 inner, ten leaves each from 0x1C024 | 0xC0 | 0xE090 | 0 | 0/0 | "Upcase Table", 0x10000, then 64 Ki entries in rows of 341 bytes (44, 42 × 8, 7 per leaf); separators are each leaf's last key |
| 0xA/0x9 logfile information | 0x1C074/0x1C078 | 0xC0 | 0xE090 | 0 | 0/2 | name, [data start, data end, length, control, control] |
| 0xD | 0x1C07C | 0x138 | 0xE0D0 | 0 | 0/4 | empty |
| 0xF (root 11) | 0x20008 | 0x278 | 0xE080 | 0 | 1/0 | one range [0, 0x3C000] FFFF0200 00010000 |
| 0x5 block refcount (root 6) | 0x2000C | 0x278 | 0xE0B0 | 0 | 1/0 | empty |
| 0x20 Container Allocator (root 2) | 0x23000 | 0x278 | 0xE010 | 0xE010 | 0xD/0 | [0x20000, 0x4000], [0x24000, 0x4000] |
| 0x21 Medium Allocator (root 1) | 0x23800 | 0x278 | 0xE010 | 0xE010 | 0xD/0 | the whole volume (below) |

**Object table rows.** A row is `[u64 2, u32 0x18, u32 0x30, u32 trailer offset 0xC8, u32
trailer length, u64 LSN]`, followed by the 0x30-byte page reference at 0x20. The trailer is 8
bytes for directories (the last file number: 1) and for 0x520 (0x500), and absent otherwise. The
value is 0xF0 bytes, or 0xF8 with a trailer.

**Allocator rows.** A row is `[start, length]` followed by a u16 free count, u16 flags, a u16
header word and a u16 tail word, then the bitmap.

- Flags 1 mark a bitmap row: 0x800 bytes, or a short one rounded up to 8 bytes.
- Flags 5 mark a bitmap row that is all free.
- Flags 2 mark a compact row: tail word 0xFFFF when allocated, 0 when free.

The Medium Allocator holds these rows, with tail words as measured:

| Range | Form |
| --- | --- |
| [0, 0x4000) | bitmap, tail 0x60 |
| [0x4000, 0x8000) | bitmap, tail 0x04 |
| [0x8000, 0xC000) | compact, allocated (the Small Allocator's band) |
| [0xC000, 0x1C000) | compact, allocated (the log) |
| [0x1C000, 0x20000) | bitmap, tail 0x7C |
| [0x20000, 0x28000) | two compact rows, allocated (the Container Allocator's bands) |
| [0x28000, 0x2C000) | bitmap, flags 5 |
| [0x2C000, 0x38000) | compact, free |
| [0x38000, 0x3C000) | bitmap, tail 0x04 |

Its root header holds the cluster count (0x80), the free count (0x88), 0x1C0E4 (0x98), 0x10000
(0xA0), 0x500 (0xA8), 0x8000 (0xB8), 0x80000029 (0xC0), 0x340000000 (0xC8) and 0x1290000 (0xD0).
The Small and Container Allocators keep their covered and free counts at 0x80 and 0x88.

## Log control block

- **Block header.** `MLog`, the signature, u32 1, u32 0x1000, the log GUID, u64 2 at 0x20, u32 1
  at 0x38 and 0x3C, and the entry offset 0x78 at 0x54.
- **Entry.** Checksum at 0x80, payload length 0xE48 at 0x98, payload offset 0x38 at 0xA0, record
  type 1 at 0xA8.
- **Payload at 0xB0.** Sequence (0x11 and 0x10 for the two blocks), data start and end, LSN
  0x10000000E twice, the log GUID, then 2, 1, 1, and 0xE / 0xD at 0xF4.
- **Checksum.** The XOR of all of the block's dwords with the checksum field zero, stored as a
  u64 at 0x80.

## Constants

`RefsFormatConstants` holds the values that are arbitrary but agreed, so matching them is the
specification:

- the 30 schema definitions;
- the three default security descriptors, with the hashes that records refer to at +0x50;
- the records of the metadata directory, the root directory and System Volume Information, with
  timestamps as placeholders;
- the 442 code units where the volume's upcase table differs from the invariant culture's
  upper-casing.
