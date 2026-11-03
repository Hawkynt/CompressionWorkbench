# ISZ (UltraISO "ISO Zipped") — on-disk notes

`FileFormat.Isz` is implemented from EZB Systems' published *ISZ File Format
Specification 1.00* (2006, <https://www.ezbsystems.com/isz/iszspec.txt>) plus
four details that the specification leaves out. Those four were taken from
the behaviour of isz-tool (<https://github.com/oserres/isz-tool>, GPL-3.0,
revision `d99f05793badd3b882a56b9135444485a78d6b41`). It was read as a
description of behaviour only; no code was taken from it. The same script is
the black-box oracle in `Compression.Tests/Isz/IszTests.cs`.

## Header (64 bytes, little-endian, packed)

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 4 | `"IsZ!"` |
| 4 | 1 | header size (64) |
| 5 | 1 | version (1) |
| 6 | 4 | volume serial number |
| 10 | 2 | sector size (2048) |
| 12 | 4 | total sectors |
| 16 | 1 | encryption: 0 none, 1 password, 2/3/4 AES-128/192/256 (CBC, after compression) |
| 17 | 8 | segment size |
| 25 | 4 | chunk count |
| 29 | 4 | chunk size (a multiple of the sector size) |
| 33 | 1 | chunk pointer length (3) |
| 34 | 1 | segment number |
| 35 | 4 | chunk table offset (0 = one stored chunk) |
| 39 | 4 | segment table offset (0 = not split) |
| 43 | 4 | data offset |
| 47 | 1 | reserved |
| 48 | 4 | **CRC of the decoded image** *(not in the spec)* |
| 52 | 4 | image size in bytes, used when there is no chunk table *(not in the spec)* |
| 56 | 4 | unknown |
| 60 | 4 | **CRC of the stored chunk bytes** *(not in the spec)* |

Both CRCs are CRC-32 with the zlib polynomial, seed `0xFFFFFFFF` and **no** final
inversion. In other words, the stored value is the bitwise complement of zlib's
`crc32()`.

## Chunk table *(obfuscation not in the spec)*

There is one entry of `ptr_len` (3) bytes per chunk. Each byte is XORed with
`B6 8C A5 DE`, cycling through the key from the first byte of the table. Each
decoded entry is a 24-bit little-endian value:

- bits 22–23: kind. 0 is all zeros (no stored bytes), 1 is stored, 2 is zlib
  (zlib-wrapped), 3 is bzip2;
- bits 0–21: length. This is the stored length, or for a zero chunk the
  decoded length.

Chunk data follows from the data offset in table order. Zero chunks take no
space.

## bzip2 chunks *(not in the spec)*

UltraISO blanks the `"BZh"` stream signature of each bzip2 chunk. A reader
restores the three bytes before decoding. Our writer never emits bzip2.

## Scope

Single-segment, unencrypted images are read and written. Split images
(`.i01`…) need sibling files and are refused, and so are encrypted images.
