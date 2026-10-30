# VMFS3 on disk

What the `Vmfs` reader relies on, written down so the implementation stands on a
specification rather than on anybody's code. VMware publishes no VMFS format
documentation. The facts below were derived from **vmfs-tools** (Christophe Fillot and
Mike Hommey, GPL-2.0, `github.com/glandium/vmfs-tools`, revision `4ab76ef`, files
`libvmfs/*.h` and the read paths of `libvmfs/*.c`). They were then checked against a
volume VMware ESX wrote in 2009: the `test.img` of `github.com/weafon/vmfs6-tool`,
restored with that project's `imager -x` and listed by `debugvmfs`. Code was not taken
from those projects; this document is the firewall (rung 2).

All integers are little-endian. "Logical" offsets are offsets in the volume's
address space, as below.

## Volume and address space

| Where | What |
|---|---|
| Partition start | 0 for an unpartitioned image; otherwise the start of the MBR partition of type `0xFB`, or of the GPT partition with type GUID `AA31E02A-400F-11DB-9590-000C2911D1B8`. |
| partition + `0x100000` | Volume info, magic `0xC001D00D` (u32). +4: version (u32; 3 for VMFS3). +0x200: LVM info: `first_segment` (u32) at +0x200+0x78, `num_extents` (u32) at +0x200+0x90. |
| partition + `0x100000` + `0x1000000` | Logical offset 0 of a single-extent volume. A volume of more than one extent is spread over several LUNs and is not read. |

## File-system info (logical `0x200000`)

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | magic `0x2FABF15E` |
| 4 | 4 | volume version |
| 8 | 1 | file-system version |
| 9 | 16 | UUID |
| 25 | 4 | mode |
| 29 | 128 | label, NUL-padded |
| 157 | 4 | device block size |
| 161 | 8 | file block size (bytes; 1 MiB on the reference volume) |
| 169 | 4 | creation time (seconds) |
| 177 | 16 | LVM UUID |
| 209 | 4 | FDC header size |
| 213 | 4 | FDC bitmap count |
| 217 | 4 | sub-block size |

## Block identifiers

A 32-bit block ID carries its type in bits 0–2. The other fields depend on the type:

| Type | Value | Layout |
|---|---|---|
| none | 0 | unallocated: reads as zeros |
| file block (FB) | 1 | item = bits 6–31; flags bits 3–5, of which value 4 (bit 5) is *to be zeroed*: read as zeros |
| sub-block (SB) | 2 | entry = bits 6–27; item = bits 28–31 (low 4) and bits 3–4 (high 2) |
| pointer block (PB) | 3 | entry = bits 6–27; item = bits 28–31 |
| file descriptor (FD) | 4 | entry = bits 6–21; item = bits 22–31 |

A file block lives at logical `item × blockSize`.

## Metadata files

Five system files sit in the root directory: `.fbb.sf` (file-block bitmap), `.fdc.sf`
(file descriptors, that is the inodes), `.pbc.sf` (pointer blocks), `.sbc.sf`
(sub-blocks) and `.vh.sf` (heartbeats). Each of the first four is a *bitmap file*. It
starts with a header:

| Offset | Field |
|---|---|
| 0x00 | items per bitmap entry |
| 0x04 | bitmap entries per area |
| 0x08 | header size |
| 0x0C | item (data) size |
| 0x10 | area size |
| 0x14 | total items |
| 0x18 | area count |

Item `(entry, item)` of a bitmap file is at file offset

    addr  = entry × itemsPerEntry + item
    area  = addr ÷ (entriesPerArea × itemsPerEntry)
    pos   = headerSize + area × areaSize + entriesPerArea × 0x400
            + (addr mod (entriesPerArea × itemsPerEntry)) × itemSize

The `0x400` is the size of one bitmap entry; each area holds its entries before its
items.

**Bootstrap.** The `.fdc.sf` inode is itself in the FDC. Its first block is file block
`max(1, (0x300000 + 2048 × 0x200) ÷ blockSize)`: the first block after the heartbeat
region (logical `0x300000`, 2048 heartbeats of 512 bytes). Reading the FDC as a
one-block file from that block reaches the root directory's inode, FD(0,0). The root
directory then names the real `.fdc.sf`, `.pbc.sf` and `.sbc.sf`.

## Inode (file descriptor, 0x800 bytes)

The first 512 bytes are a metadata header with magic `0x10C00001` at offset 0. Then:

| Offset | Size | Field |
|---|---|---|
| 512 | 4 | ID (the inode's own FD block ID) |
| 520 | 4 | link count |
| 524 | 4 | type: 2 directory, 3 file, 4 symlink, 5 system file, 6 raw device mapping |
| 532 | 8 | size |
| 540 | 8 | block size used for addressing (`blk_size`) |
| 548 | 8 | block count |
| 556 | 4 | mtime |
| 560 | 4 | ctime |
| 564 | 4 | atime |
| 568 | 4 | uid |
| 572 | 4 | gid |
| 576 | 4 | mode |
| 580 | 4 | ZLA: how the block array is read |
| 1024 | 1024 | 256 block IDs |

The ZLA says how the array is used:

- **ZLA 1 (FB) or 2 (SB):** entry `pos ÷ blk_size` is the block ID of that part of
  the file.
- **ZLA 3 (PB):** entry `(pos ÷ blk_size) ÷ n` names a pointer block, where
  `n = PBC item size ÷ 4`. That pointer block's entry `(pos ÷ blk_size) mod n` is the
  block ID.

A sub-block's bytes are the SBC item `(entry, item)`, read at `pos mod SBC item size`.
VMFS5 adds 4301 to the ZLA and can store a small file inline at inode offset 1024.
VMFS5 and VMFS6 are not read here, because no public volume exists to prove them.

## Directories

A directory's contents are 0x8C-byte records:

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | type, as the inode type |
| 4 | 4 | block ID of the entry's inode (an FD ID) |
| 8 | 4 | record ID |
| 12 | 128 | name, NUL-padded |

The directory's size covers its records. `.` and `..` are records like any other.
