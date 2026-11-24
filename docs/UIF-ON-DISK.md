# MagicISO UIF on disk

UIF (Universal Image Format) is MagicISO's compressed disc image. This page is the specification our
reader and writer are built from. It was written from the data layout and behaviour of uif2iso 0.1.7c
(Luigi Auriemma, GPL-2.0), which is a black-box oracle here (none of its code was taken), and from
images MagicISO 5.5 build 0281 wrote and read under wine.

Evidence:

- two real images published on archive.org ("Quake Shareware" and "Doom II German", items
  `QuakeShareware.7z` and `Img2843`): the ISO we extract is byte-identical to uif2iso's;
- six images MagicISO wrote from generated sources (`Compression.Tests/Uif/ReferenceVectors`): ISO,
  password-protected ISO, BIN/CUE, password-protected BIN/CUE, CloneCD and NRG. Each decodes to the
  files MagicISO was given;
- images our writer produces: uif2iso and MagicISO's console tool `miso.exe -conv iso` turn them back
  into the original ISO, and MagicISO's Tools > Decompress UIF Image does so for a password-protected one.

All integers are little-endian.

## File layout

```
[compressed runs][bsdr block, password only][blhr header + table][blms + blss, type 9 only][bbis trailer]
```

## Trailer: `bbis` (the last 64 bytes)

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 4 | `bbis` |
| 4 | 4 | trailer size (64) |
| 8 | 2 | version (1 in every image MagicISO 5.5 build 0268 and 0281 wrote) |
| 10 | 2 | image type: 8 for an ISO image, 9 for a mixed-mode image (made from CUE/BIN, MDS/MDF, CCD or NRG) |
| 12 | 2 | 1 when password-protected, else 0 |
| 14 | 2 | padding |
| 16 | 4 | sector count |
| 20 | 4 | sector size (MagicISO writes 2048 for every source kind; a raw image is cut into 2048-byte "sectors" too) |
| 24 | 4 | bytes used in the last sector, or 0 when the sector is full |
| 28 | 8 | offset of the `blhr` table, or of the `bsdr` block in a password-protected image |
| 36 | 4 | bytes from that offset to the end of the file |
| 40 | 16 | MD5 of every byte before the trailer |
| 56 | 4 | key bytes 1–4 (0 in every MagicISO image we have) |
| 60 | 4 | unknown, 0 |

MagicISO does not check the MD5 when it reads an image (`miso.exe` converts one whose hash is zeroed);
it writes it, and so do we.

## Block table: `blhr`

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 4 | `blhr` |
| 4 | 4 | stored table size plus 8 |
| 8 | 4 | 1 when the table is zlib-compressed |
| 12 | 4 | entry count |
| 16 | … | table data |

Each 24-byte entry places a run of sectors:

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 8 | file offset of the stored data |
| 8 | 4 | stored size |
| 12 | 4 | first output sector |
| 16 | 4 | sector count |
| 20 | 4 | type: 1 copied (zero-padded to the run), 3 zero-filled, 5 zlib-compressed |

The output is the sector count times the sector size; when the trailer gives the bytes used in the last
sector, the image ends there. MagicISO starts a run at every 128th sector and splits a run where sectors
change between all-zero (type 3, offset and size 0) and not (type 5). Our writer cuts runs the same way.

## Mixed-mode images (type 9)

Two blocks follow the table, both shaped like `blhr` (signature, size, compressed flag, count):

- `blms`: size = stored bytes + 8, count = decoded bytes. The decoded block is MagicISO's track layout:
  a 64-byte header whose u32 at 0x04 and 0x10 give the offsets of the NUL-terminated full paths of the
  source image and its descriptor (for example `Z:\…\mixed.bin` and `Z:\…\mixed.cue`), then 68-byte
  track records. Neither MagicISO nor uif2iso needs the track records to rebuild the image.
- `blss`: a 16-byte header, then a u32 source kind (0 ISO, 1 CUE, 2 MDS, 3 CCD, 4 NRG), then the
  stored bytes; size = stored bytes + 12, count = decoded bytes (0 when there is no descriptor, as for
  NRG). The decoded bytes are the original descriptor file, unchanged: the `.cue` or `.mds` text; for
  CloneCD two u32 lengths followed by the `.ccd` and the `.sub`.

The sectors hold the raw image (the `.bin`, `.mdf`, `.img` or `.nrg`) byte for byte. MagicISO's
Decompress UIF Image writes the image and the descriptor unchanged under the name the user picks, so a
`.cue` still names the original `.bin`. We extract them under the names stored in `blms` so the pair
still fits together, and fall back to `image.*` when the names are missing or unusable.

## Password protection

When protected, the trailer offset points at a `bsdr` block instead of the table:

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 4 | `bsdr` |
| 4 | 4 | 64 |
| 8 | 64 | the real trailer, DES-encrypted; its table offset is this block's own offset, its size field and hash are 0, its protected flag is 1 |

The `blhr` header follows the `bsdr` block (72 bytes later) in the clear. Every whole 8-byte block of the
compressed table, of each stored run, and of the compressed `blms`/`blss` data is DES-encrypted in ECB
mode (FIPS 46-3); a tail shorter than 8 bytes stays in the clear. Uncompressed header blocks are not
encrypted.

The DES key comes from the password: its first 31 bytes, zero-padded to 32, read as four little-endian
64-bit words and summed (mod 2^64); the 8 bytes of the sum are the key. MagicISO limits passwords to
31 characters. A wrong password shows at once: the inner trailer does not decrypt to `bbis`.

MagicISO's console tool `miso.exe` cannot open a protected image; its window asks for the password.

## Not supported

uif2iso also knows these, but no image MagicISO 5.5 build 0268 or 0281 writes uses them, and MagicISO
5.5 build 0281 refuses every image of them we built from uif2iso's description, so nothing proves them:

| Case | What uif2iso expects | Why refused |
| --- | --- | --- |
| Version 3 and 4 | each compressed block an LZMA stream: one x86-filter flag byte, 5 property bytes, an 8-byte size, the stream | MagicISO 5.5.281 rejects such images ("isn't CD image file") |
| Key byte 1 = 1…16, version ≥ 2 | DES under one of sixteen fixed key strings | MagicISO 5.5.281 rejects such images |
| Key byte 2 = 2 | a cascade of up to eight ciphers keyed by key bytes 1 and 3 | no image and no tool to write one |

Version 2 without a key byte reads like version 1; MagicISO 5.5.281 decodes such an image to the same
bytes we do.

## Supported

| Case | Read | Write |
| --- | --- | --- |
| Version 1/2, type 8 (ISO) | yes; matches uif2iso on two real images and MagicISO's own output | version 1 |
| Type 9 from CUE/BIN, CCD/IMG/SUB or NRG | yes; matches MagicISO-made images | no |
| Type 9 from MDS/MDF (source kind 2) | refused: no sample (MagicISO cannot make an MDS to compress) | no |
| Password protection | yes | yes (type 8) |
| Version 3/4, key-in-trailer encryption | refused | no |
