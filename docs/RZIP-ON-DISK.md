# rzip on disk

How `FileFormat.Rzip` reads and writes `.rz` files of Andrew Tridgell's rzip 2.x.

**Sources.** rzip 2.1 (`rzip.samba.org`, GPL-2.0-or-later) was read as a description of the
format — `main.c` for the file header, `stream.c` for the stream multiplexer, `runzip.c` for the
token grammar — and built with bzip2 1.0.8 as a black-box oracle that compresses and
decompresses files. Nothing below is code from it; the CRC table is the ISO 3309 table and is a
constant.

## File

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | `RZIP` |
| 4 | 1 | major version, 2 |
| 5 | 1 | minor version, 1 (rzip's reader checks neither version byte) |
| 6 | 4 | original size, low 32 bits, big-endian |
| 10 | 4 | original size, high 32 bits, big-endian (zero from builds without large-file support) |
| 14 | 10 | zero |

Then chunks, back to back, until the original size has been produced. An empty file is the
24-byte header alone. rzip makes a chunk of `level × 100 MiB` of input (`100 MiB` at level 0).

## Chunk: two multiplexed streams

A chunk carries two byte streams, interleaved in blocks. Stream 0 is the control stream; stream 1
holds the literal bytes. All positions in a chunk are relative to the chunk's first byte; all
integers here are little-endian.

A chunk starts with one 13-byte header per stream, in stream order:

| Size | Field |
|---|---|
| 1 | type: 3 stored |
| 4 | compressed length: 0 |
| 4 | uncompressed length: 0 |
| 4 | position of the stream's first block header, or 0 when the stream has no block |

Each block is a 13-byte header followed by its data:

| Size | Field |
|---|---|
| 1 | type: 3 stored, 4 bzip2 |
| 4 | compressed length (bytes of data that follow) |
| 4 | uncompressed length |
| 4 | position of the stream's next block header, or 0 |

A bzip2 block's data is one complete bzip2 stream (`BZh…`). The writer buffers up to
`100 KiB × bzip2 level` (100 KiB at level 0) per stream, writes a block when a buffer fills and
the remaining buffers at the end of the chunk, stream 0 first; it stores a block whose bzip2
form would not be smaller. The linked list makes the block order irrelevant to a reader.

A reader that finds stream 0's first header all zero, including the next-block field, skips
those 13 bytes and reads the stream headers again (rzip's "stream close workaround"). The chunk
ends after the last block that was read; the next chunk starts there.

## Tokens (stream 0)

Each token starts with a type byte and a 16-bit length:

| Type | Length | Then | Meaning |
|---|---|---|---|
| 0 | 1-65535 | — | copy that many literal bytes from stream 1 |
| 1 (any non-zero) | 1-65535 | 4-byte distance | copy that many bytes from `distance` bytes back in the output; the copy may overlap itself |
| 0 | 0 | 4-byte CRC | end of chunk |

The distance counts back from the current end of the whole output and is at least 1. Longer runs
are split into several tokens. The CRC is CRC-32 with the reflected polynomial `0xEDB88320`,
initial value 0 and no final XOR, over the bytes the chunk produced.

## What we write

One chunk per 600 MiB of input (rzip's default level 6), bzip2 level 9 blocks of up to 900 KiB,
matches from our long-range rolling-hash finder (encoder-only settings), stream 0 flushed before
stream 1 at the end of each chunk. The bytes differ from rzip's because the match finders
differ; rzip 2.1 restores every file we write, and we restore every file it writes.
