# Stacker STACVOL on-disk format

> **Driver-verification status (2026-06-18): NOT genuine / NOT driver-mountable.**
> This writer's STACVOL output is **rejected** by the independent `dmsdos` driver:
> `cvftest` detects the `"STACKER"` magic but the mount **panics** ("Too many
> clusters") because our superblock is not the genuine **obfuscated SCB**. Genuine
> Stacker XOR-obfuscates 0x30 bytes of the superblock at offset 0x50 (rolling key
> seeded at byte 0x4c: `b=0xc4-b; b = b<0x80 ? b*2 : b*2+1; b ^= *p`), keeps raw
> `0x4e=0x0a`,`0x4f=0x1a`, stores the version little-endian at decoded `0x60/0x61`
> (>=410 → v4), and uses interleaved 3-byte (12-bit FAT) / 4-byte (16-bit) MDFAT
> entries (`area = (area/6)*9 + area%6 + 3 + fatstart`). The notes below are a
> working hypothesis pending a rewrite to that genuine layout.

Clean-room notes describing STACVOL data-layout facts (field offsets, the BPB
describing the inner volume, the cluster sector-map encoding). No driver code was
copied.

A Stacker compressed volume is an ordinary MS-DOS host file (canonically
`STACVOL.DSK`, also `*.STA`/`*.STK`) that wraps a compressed inner FAT volume.
Everything is little-endian, sector size 512.

## 1. Banner sectors (physical sectors 0 and 1)

Physical sector 0 holds an ASCII banner, mirrored verbatim in sector 1:

```
STACKER  version  N    volume:  <host-path>
```

padded with spaces, terminated `0D 0A 1A` (CR LF EOF). `N` is the major
version digit (3 or 4). `<host-path>` is the DOS path of the host file, e.g.
`C:\STACVOL.DSK`. This is purely informational; the binary structures start at
sector 2.

## 2. Stacker Control Block / inner-volume BPB (physical sectors 2 and 3)

Sector 2 is a DOS boot sector / BIOS Parameter Block (BPB) describing the
**decompressed inner FAT volume**. Sector 3 is a byte-identical backup copy.

| offset | size | field | oracle value |
|-------:|-----:|-------|--------------|
| 0x00 | 3   | jump (`EB FE 90`)            | infinite-loop stub |
| 0x03 | 8   | OEM name                     | `STACKER ` |
| 0x0B | 2   | bytes per sector             | 512 |
| 0x0D | 1   | sectors per cluster          | 16 |
| 0x0E | 2   | reserved sectors             | 1 |
| 0x10 | 1   | number of FATs               | 2 |
| 0x11 | 2   | root directory entries       | 512 |
| 0x13 | 2   | total sectors (16-bit)       | 31065 |
| 0x15 | 1   | media descriptor             | 0xF8 |
| 0x16 | 2   | sectors per FAT              | 12 |
| 0x18 | 2   | sectors per track            | 63 |
| 0x1A | 2   | heads                        | 15 |
| 0x1C | 4   | hidden sectors               | 0 |
| 0x20 | 4   | total sectors (32-bit)       | 0 |
| 0x24 | 1   | physical drive number        | 0 |
| 0x26 | 1   | extended-boot signature      | 0x29 |
| 0x27 | 4   | volume serial                | 0x20620613 |
| 0x2B | 11  | volume label                 | `STACKER.VOL` |
| 0x36 | 8   | filesystem type              | (zero in 3.10) |

The decompressed inner volume is therefore a textbook FAT12 image. Its logical
layout (in inner sector units) is the standard one implied by the BPB:

```
inner 0                                  boot sector
inner 1 .. RSVD-1                         (none beyond the boot sector here)
inner RSVD .. RSVD+FATSZ-1                FAT #1
inner RSVD+FATSZ .. RSVD+2*FATSZ-1        FAT #2
... root directory (ROOTENT*32/512 sectors)
... data area (cluster 2 = first data cluster)
```

`STACVOL_DSK` appears as the inner volume label (attribute 0x08) in the root
directory; the oracle is otherwise empty.

## 3. Reserved physical region

CREATE writes the Stacker boot/loader system files (the resident driver, the
`_STAC_HI` high-loader, version string `3.10.117`, etc.) into a reserved
physical area of the host file. These live outside the inner FAT volume's
logical space and are loaded by the boot process directly by physical sector;
they are not reachable through the inner FAT and are not user data.

## 4. Cluster sector map (logical inner cluster -> physical sectors)

Inner-volume sectors are **not** stored contiguously in the host file; each
inner FAT region maps to a physical run via a sector map. In the empty oracle
the metadata regions are pre-allocated at a fixed 3:1 stride
(`physical = 9 + inner_sector * 3` for the FAT/root region), reflecting
CREATE's worst-case reservation for poorly-compressing metadata. Data clusters
are stored on demand: each cluster is written either

* **STORED** — the cluster's bytes verbatim (incompressible data), or
* **COMPRESSED** — Stac LZS (see section 5),

and a per-cluster map entry records the physical start sector and the
stored/compressed flag plus the compressed length. The genuine map lives in
the resident driver's packed image and the supplied oracle was created empty,
so the exact field packing of the production data map is not reproduced here.

This repo therefore defines an explicit, self-describing STORED sector map that
`StackerWriter` emits and `StackerReader` consumes byte-exact (round-trip
guaranteed), while still parsing the genuine banner + SCB/BPB and walking the
genuine inner FAT directory of real volumes. The map table format we emit:

```
map sector (immediately after the inner volume image), repeated entries:
  u32 logicalCluster
  u32 physicalSector
  u16 compressedLength   (0 => stored, length in bytes otherwise)
  u16 flags              (bit0: 1=compressed LZS, 0=stored)
terminated by an entry with logicalCluster == 0xFFFFFFFF
```

## 5. Compression: Stac LZS

Compressed clusters use the Stac LZS scheme published in IETF RFC 1967 /
RFC 2395 (Hi/fn LZS): a single-bit-prefixed token stream over a 2048-byte
history window.

* literal: `0` + 8-bit byte.
* match: `1` + offset + length.
  * offset: `1` + 7-bit offset (1..127), or `0` + 11-bit offset (128..2047).
  * length: 2 bits `00/01/10` -> 2/3/4; `11 00`..`11 14` -> 5..7; then
    extended nibble groups of 0x0F for longer matches per the RFC.
* end marker: offset field of all-ones (`110000000`).

The bit stream is MSB-first within each byte. This is implemented from the
public RFC specification; the driver disassembly was used only to confirm the
2048-byte window and the literal/match prefix convention.
