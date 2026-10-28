# High Sierra on disk

The CD-ROM format the High Sierra Group published in 1986, a year before ECMA-119
(ISO 9660) reworked it. This is the specification `FileSystem.HighSierra` is written
from.

**Sources.** The normative text is the *Working Paper for Information Processing: Volume and
File Structure of CD-ROM* (High Sierra Group, 1986), as scanned and transcribed by the
OS/2 Museum (<http://www.os2museum.com/files/docs/cdrom/hsg_wp.html>; PDF mirrored at
archive.org `cdrom-working-paper-1986`). Section numbers and byte positions (BP, 1-based)
below are the paper's. FreeBSD's BSD-3-Clause `sys/fs/cd9660/iso.h`
(`struct iso_sierra_primary_descriptor`) and `cd9660_node.c` were used to cross-check them,
and agree everywhere except one field, noted below. The Linux `isofs` driver acts as the
oracle.

## Volume space (6.1–6.3)

Logical sectors are 2048 bytes. Sectors 0–15 are the System Area. Volume descriptors
start at sector 16, one per sector, and end with one or more Volume Descriptor Sequence
Terminators.

## Volume descriptor header (11.1)

| BP | Field |
| --- | --- |
| 1–8 | Volume Descriptor LBN, both byte orders (10.3.3). ISO 9660 has no such field |
| 9 | type: 0 boot record, 1 Standard File Structure, 2 Coded Character Set, 3 Unspecified, 255 terminator |
| 10–14 | `CDROM` (ISO 9660: `CD001` at BP 2–6) |
| 15 | version, 1 |

## Standard File Structure Volume Descriptor (11.4)

| BP | Field |
| --- | --- |
| 16 | reserved (00) |
| 17–48 | System Identifier (a-characters) |
| 49–80 | Volume Identifier (d-characters) |
| 89–96 | Volume Space Size, logical blocks, both orders |
| 129–132 / 133–136 | Volume Set Size / Sequence Number, 16-bit both orders |
| 137–140 | Logical Block Size, 16-bit both orders |
| 141–148 | Path Table Size, bytes, both orders |
| 149–152 | first mandatory Path Table, **LSB-first** table |
| 153–164 | three optional LSB-first tables (0 = absent) |
| 165–168 | second mandatory Path Table, **MSB-first** table |
| 169–180 | three optional MSB-first tables |
| 181–214 | root Directory Record (34 bytes) |
| 215–342 | Volume Set Identifier |
| 343–470 / 471–598 / 599–726 | Publisher / Data Preparer / Application Identifier |
| 727–758 | Copyright File Identifier (32 bytes) |
| 759–790 | Abstract File Identifier (32 bytes) |
| 791–806 / 807–822 / 823–838 / 839–854 | creation / modification / expiration / effective date, 16 ASCII digits `YYYYMMDDHHMMSScc`, all `0` = unspecified (11.4.30.1). No time-zone byte |
| 855 | File Structure Standard Version, 1 |

FreeBSD's struct gives a single 64-byte `copyright_id` at BP 727–790. The working paper
splits that range into the copyright and abstract file identifiers. We follow the paper.

## Path Table Record (12.1.1)

| BP | Field |
| --- | --- |
| 1–4 | extent location (byte order of the table) |
| 5 | Extended Attribute Record length |
| 6 | identifier length |
| 7–8 | parent directory number |
| 9– | identifier (root: one (00) byte), then a (00) pad after an **odd** length |

ISO 9660 changed this order to put the identifier length first. Records are ordered by
level, then by parent number, then by identifier padded with spaces (8.2.1).

## Directory Record (13.1)

| BP | Field |
| --- | --- |
| 1 | record length |
| 2 | Extended Attribute Record length (logical blocks before the data) |
| 3–10 | extent location, both orders |
| 11–18 | data length, both orders |
| 19–24 | **6-byte** date: years since 1900, month, day, hour, minute, second; no time zone |
| 25 | File Flags — bit 0 existence (hidden), 1 directory, 2 associated file, 3 record, 4 protection, 7 multi-extent |
| 26 | reserved (00) |
| 27 / 28 | interleave size / skip factor (logical blocks) |
| 29–32 | volume sequence number, 16-bit both orders |
| 33 | identifier length |
| 34– | identifier, then a (00) pad after an **even** length (13.1.13) |

ISO 9660 adds a seventh date byte (time zone), which moves the flags to BP 26. The first two
records of every directory are `(00)` (itself) and `(01)` (its parent). A record never
crosses a logical sector, and a zero length byte pads to the next one (8.3.1). Directory
records are ordered by name, then extension, then version (8.3.2). Associated files are
ignored in interchange.

File identifiers are `NAME.EXT;VERSION` in d-characters (`A-Z 0-9 _`); the FULL STOP and
the version are optional (10.5). Directory identifiers are d-characters. The hierarchy has
at most eight levels (8.1.1).

## What the implementation does with it

* **Read**: the first Standard File Structure Volume Descriptor; directories walked from
  the root, with loops refused. Multi-extent files are concatenated, interleaved extents
  are read unit by unit, associated files are skipped, and the existence flag is reported
  as hidden. The `;version` and an empty extension's FULL STOP are removed from names.
* **Create (WORM)**: one descriptor and a terminator, the two mandatory path tables,
  directories in path-table order and file data, all with 2048-byte blocks. Lower case is
  folded to capitals. Any other non-d-character, a second FULL STOP, a file identifier over
  30 characters, two names that fold together, or a ninth level is refused.
* **Proof**: the Linux 6.8 `isofs` driver mounts the written volume, a tree up to eight
  levels deep with a multi-sector directory and an empty directory, and reads every file
  back byte-exact (`HighSierraKernelMountTests`).
