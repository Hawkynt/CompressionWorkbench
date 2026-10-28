# FLEX disks on disk

FLEX is the disk operating system Technical Systems Consultants wrote for the 6800 and 6809
from 1976. Its disks are usually preserved as raw `.dsk` sector dumps. This page is the
specification `FileSystem.Flex` is written from.

**Sources (rung 3, normative).** Both manuals are TSC's own, from the FLEX User Group archive:
* *6809 FLEX Adaptation Guide*, §2.3 (disk format) and §9.1 (NEWDISK, SIR table):
  <http://flexusergroup.com/flexusergroup/pdfs/6809fadg.pdf>
* *FLEX Advanced Programmer's Guide*, the FCB directory-information bytes 4–27,
  "Description of a directory sector", "Description of a data sector", "Random files" and
  "Diskette Initialization": <http://www.flexusergroup.com/flexusergroup/pdfs/flexapg.pdf>

`flexfloppy` from laurent-fr/flextools (no licence, so used only as a black-box oracle)
and the TSC FLEX 9.1 distribution disk `TSCFLX09.DSK` (fufu archive, sha256 `cd61d5f3…ac5c`)
confirm the details.

## Image geometry

A `.dsk` is a raw dump of 256-byte sectors with no header. Every track has the same number
of sector slots, and sector numbers start at 1:
`offset = (track × sectors_per_track + sector − 1) × 256`. The geometry comes from the SIR.

## Sectors

Every sector except the two boot sectors is laid out as:

| Bytes | Content |
| --- | --- |
| 0–1 | link: track and sector of the next sector in the chain; `00 00` ends it |
| 2–3 | logical record number, big-endian (1, 2, … in a file; 0 in the free chain) |
| 4–255 | 252 data bytes |

FLEX records no byte length: a file is its sectors' data bytes, whole.

## Track 0

| Sector | Use |
| --- | --- |
| 1–2 | boot loader |
| 3 | System Information Record (SIR) |
| 4 | reserved |
| 5–last | the directory chain, linked; its last sector's link is zero. The directory may grow onto other tracks |

## SIR (track 0 sector 3; offsets decimal, from the Adaptation Guide §9.1.5)

| Bytes | Content |
| --- | --- |
| 0–15 | zero (0–1 clear the link) |
| 16–26 | volume name, ASCII |
| 27–28 | volume number, binary (big-endian) |
| 29–30 | first sector of the free chain (track, sector) |
| 31–32 | last sector of the free chain |
| 33–34 | number of sectors in the free chain. NEWDISK writes the total data sectors, which excludes track 0 |
| 35–37 | date: month, day, year (binary) |
| 38 | highest track number |
| 39 | highest sector number (sectors per track) |

Measured on `TSCFLX09.DSK`: `FLEX-SWT`, volume 6809, 35 tracks × 10 sectors, 161 free.
The free chain's tail is not at the end of the disk: deleted files are appended to the
tail.

## Directory sector

Bytes 0–3 hold the link and record number; bytes 4–15 are unused. Ten 24-byte entries
follow, from byte 16. An entry (FCB bytes 4–27):

| Bytes | Content |
| --- | --- |
| 0–7 | name, NUL-padded; first byte `00` = never used, negative = deleted |
| 8–10 | extension, NUL-padded |
| 11 | attributes: bit 7 write-, 6 delete-, 5 read-, 4 catalog-protect |
| 12 | reserved |
| 13–14 | first sector (track, sector) |
| 15–16 | last sector |
| 17–18 | sector count, big-endian |
| 19 | File Sector Map indicator: non-zero (usually 2) = random file. Its first two sectors are the map, record 0 |
| 20 | reserved |
| 21–23 | creation date: month, day, year (tens and units) |

Names start with a letter and contain letters, digits, `-` and `_`; FLEX stores them upper
case, and "files with null extensions should not be created". Deleted entries on real disks
carry `$FF` as the first byte, and that is the only marker `flexfloppy` skips.

## What the implementation does with it

* **Read**: the SIR, the directory chain (loops refused), and every live entry. File bytes
  are the 252 data bytes of every sector in the chain. A random file's two map sectors are
  included, as stored, which is what `flexfloppy` extracts. A chain whose length or end
  sector disagrees with its entry is refused.
* **Create**: the NEWDISK layout — directory linked through track 0, every data sector in
  one free chain, SIR filled in. The geometry is chosen from 35×10, 40×18, 77×15, 80×36 or
  255×255, or given. Files are written as FMS writes them.
* **Edit in place**, FMS style. Add takes sectors from the head of the free chain, uses the
  first never-used or deleted directory slot (growing the directory chain by one sector when
  none is free), numbers the records from 1, and updates the SIR. Remove refuses a
  delete-protected file. Otherwise it sets the entry's first byte to `$FF`, zeroes the
  file's record numbers and data, appends its chain to the tail of the free chain, and
  updates the SIR. A free chain that disagrees with the SIR's head, tail or count is
  refused, with the image unchanged.
* Contents are whole records. A file whose length is not a multiple of 252 comes back
  zero-padded, and an empty file takes one sector.
