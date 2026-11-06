# MagicISO UIF on disk

UIF (Universal Image Format) is MagicISO's compressed disc image. This page is the specification our
reader is built from. It was written from the behaviour and data layout of uif2iso 0.1.7c (Luigi
Auriemma, GPL-2.0), which is our oracle; none of its code was taken. The layout was checked on two
real MagicISO images published on archive.org ("Quake Shareware" and "Doom II German", items
`QuakeShareware.7z` and `Img2843`). `UifExternalConformanceTests` pins the result: the ISO we extract
is byte-identical to uif2iso's.

All integers are little-endian.

## Trailer: `bbis` (the last 64 bytes)

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 4 | `bbis` |
| 4 | 4 | trailer size (64) |
| 8 | 2 | version (1 in both samples) |
| 10 | 2 | image type: 8 for an ISO image, 9 for a mixed-mode image with a cue/mds/ccd/nrg descriptor |
| 12 | 2 | unknown |
| 14 | 2 | padding |
| 16 | 4 | sector count |
| 20 | 4 | sector size (2048 in both samples) |
| 24 | 4 | bytes used in the last sector, or 0 when the sector is full |
| 28 | 8 | offset of the `blhr` block table |
| 36 | 4 | size of the block table and trailer |
| 40 | 16 | hash, used with passwords |
| 56 | 4 | key bytes 1–4 |
| 60 | 4 | unknown |

## Block table: `blhr`

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 4 | `blhr` (a password-protected image has `bsdr` here) |
| 4 | 4 | size of the table data plus 8 |
| 8 | 4 | 1 when the table data is zlib-compressed |
| 12 | 4 | entry count |
| 16 | … | table data |

Each 24-byte entry places a run of sectors:

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 8 | file offset of the stored data |
| 8 | 4 | stored size |
| 12 | 4 | first output sector |
| 16 | 4 | sector count |
| 20 | 4 | type: 1 copied (zero-padded to the run), 3 zero-filled, 5 compressed |

Compressed runs are zlib streams in versions 1 and 2, and an LZMA stream with a leading x86-filter
flag in version 3. The output is the sector count times the sector size. When the trailer gives the
bytes used in the last sector, the image ends there.

## Encryption

An image is encrypted in either of these cases:

- key byte 2 is 2, which selects MagicISO's own cipher;
- the version is 2 or higher and key byte 1 is between 1 and 16, which selects DES with one of
  sixteen fixed keys.

A password-protected image carries a `bsdr` block where the table would be. Neither sample is
encrypted, so encryption cannot be proven and we refuse it.

## Supported

| Case | Status |
| --- | --- |
| Version 1/2, type 8 | read; matches uif2iso on two real images |
| Version 3 (LZMA) | refused: no sample to prove it |
| Type 9 (mixed mode) | refused: no sample to prove it |
| Any encryption | refused: no sample to prove it |
| Writing | refused: no free tool writes UIF |
