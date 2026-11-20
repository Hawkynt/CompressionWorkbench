# SQX recovery records

What an SQX recovery record holds, as `FileFormat.Sqx` creates and uses it. The field list is
from the SQX 1.1 format notes (section 7.6); the block size, CRC forms, parity grouping and
counts were measured on records Squeez CommandLine 5.2 writes (`-rr1` to `-rr5`, external with
`-rre`) for archives of 3 KB to 2 MB, and on the SQX SDK's `DamagedArchive.sqx` (internal).

| Offset | Size | Field |
| ---: | ---: | --- |
| 0 | 2 | low 16 bits of the raw CRC-32 (no final inversion) of bytes 2-48 |
| 2 | 1 | type `0x58` |
| 3 | 2 | flags, 0 |
| 5 | 2 | header size, 49 |
| 7 | 5 | `SQ4RD` |
| 12 | 4 | level: percent of recovery data |
| 16 | 8 | N: protected blocks, ceil(protected size / 512) |
| 24 | 8 | R: parity blocks, ceil(N × level / 100), at least 1 |
| 32 | 1 | format, 11 |
| 33 | 8 | protected size |
| 41 | 4 | raw CRC-32 of everything after the header (the block CRCs and the parity) |
| 45 | 4 | raw CRC-32 of the protected data (external records); 0xFFFFFFFF in an internal one |
| 49 | 2 × N | per protected block, the low 16 bits of the raw CRC-32 of its 512 bytes (the last block zero-padded) |
| 49 + 2N | 512 × R | parity block k: the XOR of every protected block whose index is k modulo R |

An external record is the file `name.sqx.sqr` and protects the whole archive. An internal record
follows the data it protects inside the archive (its protected size is its own offset); the
reader skips its CRCs and parity. Any one damaged block per parity group can be rebuilt; a
rebuilt block must match its CRC.
