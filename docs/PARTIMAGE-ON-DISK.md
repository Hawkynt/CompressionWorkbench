# partimage on disk

partimage 0.6.x saves the used blocks of a partition, together with headers and a block bitmap. Our
reader is converted from imagebackup's `partimage.py` (MIT; attribution in
`Hawkynt.FileFormats.FileSystems/FileFormats/FileFormat.PartImage/THIRD-PARTY-NOTICE.imagebackup.txt`).
partimage 0.6.9 is the oracle: `PartImageExternalConformanceTests` checks that our extraction of an
image equals what `partimage restore` writes from it.

All integers are little-endian.

## Compression

With `-z1` the whole file is one gzip stream, and with `-z2` one bzip2 stream. The volume header is
compressed as well. The main header records the choice: 0 none, 1 gzip, 2 bzip2.

## Volume header (512 bytes)

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 32 | `PaRtImAgE-VoLuMe`, zero-padded |
| 32 | 64 | version string, e.g. `0.6.1` |
| 96 | 4 | volume number (0 for the first file of a split image) |
| 100 | 8 | image identifier, the same in every volume |

## Headers with a checksum (16,388 bytes each)

The main, local and info headers are each 16,384 bytes of data followed by a 4-byte checksum. The
checksum is the signed sum of the data bytes, taken as `int8`.

**Main header**, directly after the volume header:

- strings, NUL-padded: file system (512), description (4096), device (512), first path (4095),
  sysname, nodename, release, version and machine (65 each);
- compression (4) and flags (4);
- the save time as eleven `uint32` fields of a C `struct tm`;
- partition size in bytes (8);
- hostname (128) and version (64);
- MBR count (4), MBR size (4) and encryption algorithm (4).

Segments follow. Each one opens with an ASCII marker `MAGIC-BEGIN-<NAME>`:

| Segment | Content |
| --- | --- |
| `MBRBACKUP` | the saved MBR copies, each introduced by its own marker; skipped |
| `LOCALHEADER` | block size (8), used blocks (8), block count (8), bitmap size (8), bad blocks (8), label (64), then checksum |
| `BITMAP` | *bitmap size* bytes; bit *n* of byte *n*/8 (least significant first) is set when block *n* is used |
| `INFO` | the file-system-specific info header, checksummed |
| `DATABLOCKS` | the used blocks in ascending order |
| `TAIL` | sum (8), volume number (4) |

## Data blocks and checks

The used blocks follow one another without separators. After every 64 KiB of blocks, a 16-byte check
record follows: `CHK\0`, a CRC-32 (8), and a block number (8).

- The CRC-32 is the standard (zlib) CRC-32 of all blocks since the previous check.
- The block number is the first block of the current run of used blocks. Runs are cut into pieces of
  at most 256 KiB / block size blocks, and the block number refers to the current piece.

Bits beyond the block count are ignored. A restored partition holds the used blocks in place and
zeros everywhere else. It is as long as the partition size, or as the block count times the block
size if that is longer.

## Tail

`MAGIC-BEGIN-TAIL` is followed by:

- a 64-bit sum of every byte from offset 512 (the end of the volume header) through the tail marker
  itself;
- the volume number.

A split image (`-V`) continues in `name.001`, `name.002`, and so on, each with its own volume
header. We read only complete single-volume images and refuse a later volume.
