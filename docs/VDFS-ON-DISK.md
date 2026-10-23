# VDF volumes (ZenGin "VDFS") on disk

The virtual-disk volumes Piranha Bytes' ZenGin (Gothic, Gothic II) loads its data from,
`.vdf` and `.mod`, plus the zipped variant Union's tools write. This is **not** Samsung's
VDFS flash filesystem, which shares only the name.

## Sources

| Rung | Source | Used for |
|------|--------|----------|
| 1 | GothicKit **ZenKit** (MIT), `src/Vfs.cc`, `src/Stream.cc`, commit `e1c1044e4e0708b05db152ede44393f78d935ad5` | layout, flags, ZippedStream framing |
| oracle | ZenKit built with `ZK_ENABLE_ZIPPED_VDF=ON` | reading what we write, writing what we read |
| real files | `tests/samples/basic.vdf` (GothicVDFS), `basic_zipped.vdf` (VDFSTool), from ZenKit's test corpus | every constant below, measured |

We reimplemented the layout in our own code rather than vendoring ZenKit. Its C++ depends
on its own stream and node types, and we needed less than a page of it.

## Volume header (296 bytes)

| Offset | Size | Field |
|-------:|-----:|-------|
| 0 | 256 | comment, padded with `0x1A`; readers stop at the first `0x1A` |
| 256 | 16 | signature: `PSVDSC_V2.00\r\n\r\n` (Gothic 1), `PSVDSC_V2.00\n\r\n\r` (Gothic 2), `PSVDSC_V2.00\x1A\x1A\x1A\x1A` (VDFSTool) |
| 272 | 4 | catalog entry count, directories included |
| 276 | 4 | file count |
| 280 | 4 | timestamp, MS-DOS date/time |
| 284 | 4 | sum of all files' **uncompressed** sizes (the same in a zipped volume: 43 804 in both samples) |
| 288 | 4 | catalog offset; 296 in every volume seen; 0 means "right here" |
| 292 | 4 | volume flags: `0x50` stored, `0xA0` zipped. ZenKit warns on anything else and treats the volume as stored |

All integers are little-endian.

## Catalog (80 bytes per entry)

| Offset | Size | Field |
|-------:|-----:|-------|
| 0 | 64 | name of one path component, space-padded. The reference tools upper-case it |
| 64 | 4 | file: absolute payload offset · directory: index of its first child entry |
| 68 | 4 | file: uncompressed size · directory: 0 |
| 72 | 4 | type: `0x80000000` directory, `0x40000000` last entry of its directory |
| 76 | 4 | attributes: `0x20` (archive) on files, 0 on directories |

A directory's entries are contiguous and end at the entry flagged `0x40000000`. Reading
starts at index 0. Both samples lay the catalog out this way:

* within a block, subdirectories come first and files after them, each group sorted by name;
* subdirectory blocks follow depth-first, and each directory entry points at its block;
* payloads follow the catalog in post-order: a directory's subdirectories' files first,
  then its own files.

Our writer produces `basic.vdf` byte for byte from its files, comment and timestamp
(`VdfsReferenceVectorTests.Writer_ReproducesTheGothicVdfsVolume_ByteForByte`).

An empty directory cannot be written. Its child index would name the first entry of an
unrelated block, so a reader would walk into that block looking for a last entry. Our
writer leaves empty directories out. ZenKit's writer does not, which is a bug in ZenKit.

## Zipped volumes: Union ZippedStream

In a volume flagged `0xA0` the catalog stays as above and its sizes stay uncompressed.
Each file's payload is a ZippedStream:

```
u32 length        uncompressed bytes (equals the catalog size)
u32 blockSize
u32 blockCount    = ceil(length / blockSize)
blockCount × {
  u32 sourceLength       bytes this block inflates to (≤ blockSize; only the last is shorter)
  u32 compressedLength
  u32 blockSize          repeated
  u8  data[compressedLength]   a complete zlib stream (RFC 1950 header + Adler-32)
}
```

Measured on `basic_zipped.vdf`: VDFSTool uses `blockSize = length / 4` (integer division),
which can produce a fifth, short block (7675 → 4 × 1918 + 3). Its zlib header is `78 DA`
(level 9). ZenKit writes 8 KiB blocks and states that this is Union's default. Both tools
read either. Our writer uses 8 KiB.

Files ending `.wav` or `.ogg` are stored raw even in a zipped volume, because Union streams
sound straight from the volume. Readers tell the two apart by trying the stream header. A
header passes only if the block count matches the length. Our reader also requires the
header length to equal the catalog size, so a raw payload that happens to start with
plausible numbers is not taken for a stream. Empty files are always raw: ZenKit rejects a
zero-length stream header.

## What is refused

* **Unreadable volumes.** A volume with no signature at offset 256 is not read. This
  repository used to write a made-up layout with the signature at offset 0. Those images
  were never readable by any Gothic tool and are not read now.
* **Damaged ZippedStreams.** A block that does not inflate to its stated length fails the
  extraction with `InvalidDataException`. Partial output is never returned as if it were
  the file.
* **Editing a damaged catalog.** In-place editing refuses a catalog that cannot be followed
  in full, because rewriting it would drop whatever the damaged part still names. Listing
  such a volume shows what can be read plus `metadata.ini` with `parse_status=partial`.

There is no encryption or signing in either format, so nothing in that category needs
refusing.
