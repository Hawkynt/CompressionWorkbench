# DxLib archives (DXA) on disk

The archive format of the DxLib game library (山田 巧): `.dxa` files, and the `.wolf` data
archives of WOLF RPG Editor games, which are DXA archives under the editor's own password.
`FileFormat.DxArchive` reads versions 6 and 8 from this document.

## 🔎 Provenance

- **Version 6** follows GARbro's DxLib reader (`ArcFormats/DxLib/ArcDX.cs`, `DxKey.cs`, morkt,
  MIT; revision `b09ee457`), converted: header and index layout, key derivation, key recovery
  from the header, and the LZ decoder.
- **Version 8** was written down from the archiver source DxLib ships with its tools
  (`Tool/DXArchive/Source`, `DXArchive.cpp` and `Huffman.cpp`, DxLib 3.25a). That source carries
  no licence statement, so it was read to write this section and the implementation follows the
  section; constants (the default password bytes, the CRC-32 polynomial) are copied.
- **Oracle.** The archivers themselves: `DxaEncode.exe` 1.10 (DxLib 3.25a, writes version 8) and
  1.06 (DxLib 3.19b, writes version 6) made the reference archives from known files, with the
  default password, a password, and the options for no encryption, no compression and full
  Huffman coding.

## 🗂️ Version 8

All integers little-endian. The 64-byte header is stored in the clear:

| Offset | Size | Field |
| ---: | ---: | --- |
| 0 | 2 | `"DX"` |
| 2 | 2 | version (8) |
| 4 | 4 | size of the index (name, file and directory tables) once expanded |
| 8 | 8 | offset of the first file's data |
| 16 | 8 | offset of the index in the archive |
| 24 | 8 | file table offset, within the expanded index |
| 32 | 8 | directory table offset, within the expanded index |
| 40 | 4 | code page of the names (932, 1252, 65001, 1200, ...) |
| 44 | 4 | flags: 1 = no encryption, 2 = index not compressed |
| 48 | 1 | Huffman span in KiB (255 = whole file) |
| 49 | 15 | reserved |

**Keys.** A key is 7 bytes made from a byte string `s`: when `s` is shorter than 4 bytes the
8 bytes `44 58 42 44 58 41 52 43` are appended; then the bytes at even positions and those at
odd positions are each run through CRC-32 (reflected 0xEDB88320, initial and final XOR
0xFFFFFFFF) to give `c0`, `c1`, and the key is `c0` (4 bytes LE) followed by the low three
bytes of `c1`. Encrypting XORs data with the key, the key position being the given position
modulo 7. The archive password defaults to `44 58 42 44 58 41 52 43`.

**Index.** Read from the index offset to the end of the file and XOR it with the archive key
from position 0. Unless flag 2 is set it is Huffman-coded and the Huffman output is LZ-coded
(both below); expanding both gives the index.

- *Names* (from offset 0): per name a 16-bit count `n` of 4-byte units, a 16-bit parity, `4n`
  bytes of the name with ASCII letters in upper case, then `4n` bytes of the name as written,
  NUL-padded.
- *File entry* (72 bytes): name offset (8), attributes (8; 0x10 directory), created, accessed,
  written (8 each, FILETIME), data offset (8; for a directory its directory entry offset), size
  (8), LZ size (8; all ones when stored), Huffman size (8; all ones when not Huffman-coded).
- *Directory entry* (32 bytes): its own file entry offset (all ones for the root), parent
  directory offset (all ones for the root), number of entries (8), first file entry offset (8).
  The root is the directory entry at offset 0.

**File data** starts at the data offset plus the entry's data offset. Its key is made from the
password bytes followed by the upper-case name of the file and then the upper-case names of its
directories from the innermost outwards (the root has none), each up to its NUL (two NUL bytes
for UTF-16 code pages). The key position starts at the file size.

- Huffman size and LZ size both set: read Huffman-size bytes and Huffman-decode them. When the
  span is not 255 and the LZ size exceeds twice the span in KiB, the decoded bytes are the first
  and last span KiB of the LZ data, and the middle `LZ size - 2·span·1024` bytes follow in the
  file, decrypted from key position `size + Huffman size`. Then LZ-decode.
- Only the LZ size set: read and LZ-decode it.
- Only the Huffman size set: as the first case, with `size` in place of the LZ size and no LZ
  step.
- Neither: the file is stored, `size` bytes.

**LZ.** A 9-byte head: expanded size (4), coded size including the head (4), key byte `k`. Then
literal bytes, except where `k` appears: `k k` is a literal `k`; otherwise the next byte `c`
(minus 1 when greater than `k`) gives a copy: length `c >> 3`, plus one more byte shifted left 5
when bit 2 is set, plus 4; distance size by the low two bits (1, 2 or 3 bytes, little-endian),
plus 1. Copies may overlap their output.

**Huffman.** A bit stream read most significant bit first: 6 bits `a`, then the expanded size
in `a + 1` bits; 6 bits `b`, then the coded size in `b + 1` bits; then 256 frequencies, each 3
bits `w` (width `2·(w + 1)`), a sign bit and the width's bits, the first an absolute value and
each later one a 16-bit wrapping difference from the previous. The data starts at the next byte.
The tree is built over the 256 frequencies: repeatedly the two remaining nodes of least weight
(scanning in index order; the first of equal weights wins the least place, and a node replaces
the second only when strictly lighter) become children 0 and 1 of a new node numbered from 256
upwards, until node 510 is the root. Codes are read from node 510 down, one bit at a time, each
byte consumed from its least significant bit.

## 🗂️ Version 6

The header is 48 bytes, all of it encrypted with the archive key from file position 0: `"DX"`,
version 6 (2), index size (4), data offset (8), index offset (8), file table (8), directory table
(8), code page (4), 4 reserved. The key is 12 bytes: the password in code page 932, repeated to
12 bytes (twelve 0xAA when empty), then transformed byte by byte: 0 XOR 0xFF; 1 rotated right 4;
2 XOR 0x8A; 3 rotated right 4 and inverted; 4 XOR 0xFF; 5 XOR 0xAC; 6 XOR 0xFF; 7 rotated right
3 and inverted; 8 rotated left 3; 9 XOR 0x7F; 10 rotated right 4 then XOR 0xD6; 11 XOR 0xCC.
Encryption XORs with the key at the position modulo 12. A key can also be recovered from the
header alone: bytes 0-2 and 8 of the header plaintext are known (`D`, `X`, 6, 0x30), the data
offset's high word is 0, and the index offset is consistent with the file.

The index (index-size bytes at the index offset) is encrypted from key position 0 and is not
compressed. Names are as in version 8. File entries are 64 bytes: name offset, attributes,
three times, data offset, size, LZ size (all ones when stored), 8 bytes each. Directory entries
are as in version 8. File data, at data offset plus the entry's offset, is encrypted from key
position equal to the file's size, and LZ-coded as in version 8 when its LZ size is set.
