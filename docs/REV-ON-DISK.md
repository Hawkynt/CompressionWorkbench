# RAR recovery volumes (`.rev`) on disk

RAR's recovery volumes rebuild missing or damaged volumes of a multi-volume RAR archive. RARLAB
publishes no description of them. This document records what was established from two sources:
reading how the freeware UnRAR source (`recvol3.cpp`, `recvol5.cpp`, UnRAR 7.1.10; its licence
forbids using it to build a RAR-compatible archiver, so it is evidence, not something to copy) checks
and consumes these files, and measuring `.rev` files that RAR 7.12 for Linux wrote (see
`Compression.Tests/Rev/ReferenceVectors/README.md`). Only the parts needed to identify and verify a
recovery volume are described; the Reed-Solomon coding of the recovery data is not.

## RAR 5.0 recovery volumes (`rar -ma5 -rvN`)

All integers little-endian.

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 8 | signature `52 61 72 21 1A 52 65 76` ("Rar!", 0x1A, "Rev") |
| 8 | 4 | header CRC32 (standard CRC-32) over the 4-byte header size **and** the header bytes |
| 12 | 4 | header size *H* (5 < *H* ≤ 0x100000), counted from offset 16 |
| 16 | 1 | version, 1 |
| 17 | 2 | number of data volumes *D* |
| 19 | 2 | number of recovery volumes *R* |
| 21 | 2 | number of this recovery volume, counted over all *D* + *R* volumes (so *D* ≤ n < *D* + *R*) |
| 23 | 4 | CRC32 of everything after the header (offset 16 + *H* to the end of the file) |
| 27 | 12 × *D* | per data volume: UInt64 size, UInt32 CRC32 of the volume |
| 16 + *H* | rest | recovery data |

UnRAR ignores a volume whose header CRC, version or numbering does not hold, and its test command checks
the recovery data against the CRC at offset 23. Measured on RAR 7.12 output: four 3072/3072/3072/348-byte
data volumes and two recovery volumes give D = 4, R = 2, numbers 4 and 5, H = 59 (11 + 12 × 4), and the
twelve-byte records hold exactly the sizes and zlib CRC32s of the data volumes.

## RAR 3.x recovery volumes, "new style" (RAR 3.10 and later, `name.partN.rev`)

There is no header. The last seven bytes are a trailer:

| Offset from end | Size | Field |
| --- | --- | --- |
| −7 | 1 | number of data volumes − 1 |
| −6 | 1 | number of recovery volumes − 1 |
| −5 | 1 | number of this recovery volume − 1 (1-based after adding 1) |
| −4 | 4 | CRC32 (little-endian) of everything before these four bytes |

The rest of the file before the trailer is recovery data. Measured on RAR 6.24 `-ma4` output for four data
volumes and two recovery volumes: trailers `03 01 00` and `03 01 01`, and the CRC32 covers every byte before
it. "Old style" RAR 3.0 volumes
(`name#_#_#.rev`) carry their numbers only in the file name and no CRC; they are not recognisable
from their content and are not handled.
