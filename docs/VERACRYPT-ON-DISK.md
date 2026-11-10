# VeraCrypt / TrueCrypt — on-disk notes

`FileFormat.VeraCrypt` opens, mounts and creates VeraCrypt and TrueCrypt containers. Reading
covers every TrueCrypt release from 1.0, including the LRW and CBC modes it dropped in 5.0, and
every current VeraCrypt cipher and key derivation.

## Sources

| Part | Source | Rung |
| --- | --- | --- |
| Header layout, KDF table, PIM formulas, cipher-chain table, header decryption, keyfile pool, header offsets | cryptsetup `lib/tcrypt/tcrypt.c` (LGPL-2.1-or-later), converted — see `THIRD-PARTY-NOTICE.cryptsetup.txt` | 1 |
| Sector XTS | IEEE 1619 | 3 |
| Sector LRW, CBC whitening, outer/inner CBC, 64-bit LRW | TrueCrypt 4.3a `Common/Crypto.c` (TrueCrypt License — read for the specification below and compiled standalone as an oracle, never converted) | 2 |
| Serpent S-box circuits | Botan `serpent_sbox.h` (BSD-2), converted — `Compression.Core/Crypto/THIRD-PARTY-NOTICE.Botan.txt` | 1 |
| Camellia, Kuznyechik, CAST-128 | RFC 3713, RFC 7801, RFC 2144 | 3 |

The oracles, all kept as tests:

- **cryptsetup's TCRYPT test images.** These were made by TrueCrypt 1–7.1 and VeraCrypt, and each
  holds a FAT filesystem with serial DEAD-BABE (CAFE-BABE when hidden). All 76 open, and every one
  whose data survived decrypts to that serial (`TcryptImageConformanceTests`).
- **TrueCrypt 4.3a's own sector encryption**, run on fixed inputs for every legacy algorithm and
  mode (`ReferenceVectors/`, `TcLegacyVectorTests`). It fills the gap left by the test images,
  where cryptsetup wiped the data of the Blowfish and CBC-cascade containers.
- **VeraCrypt 1.26's CLI**, used both ways. It creates a container for every cipher, cascade and
  PRF, and we decrypt what it wrote. It also mounts ours, sparse ones included
  (`VeraCryptExternalConformanceTests`).
- **cryptsetup `tcryptOpen`**, which maps our VeraCrypt- and TrueCrypt-format containers to our
  plaintext.

## Container layout

| Offset | Content |
| --- | --- |
| 0 | standard volume header (512 bytes) |
| 64 KiB | hidden volume header (random bytes when there is none) |
| 128 KiB | data area (TrueCrypt 5+, VeraCrypt) |
| end − 128 KiB | backup standard header |
| end − 64 KiB | backup hidden header |

TrueCrypt before 6.0 used a different layout: the data area starts at 512, and the hidden header
sits 1536 bytes before the end, with its data area right before that.

## Header

The first 64 bytes are a salt. The remaining 448 bytes are encrypted with the header key, and they
start at data unit 0 (XTS) or LRW block index 1. The header key is 192 bytes of key derivation
over the password and the salt.

Decrypted, all integers big-endian:

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 4 | magic `TRUE` or `VERA` |
| 4 | 2 | header version |
| 6 | 2 | minimum program version |
| 8 | 4 | CRC-32 of the key area (bytes 192–447) |
| 28 | 8 | hidden volume size |
| 36 | 8 | volume size (0 before TrueCrypt 5: to the end of the container) |
| 44 | 8 | data offset (0 means 512) |
| 52 | 8 | data size |
| 60 | 4 | flags (bit 0: system encryption) |
| 64 | 4 | sector size (0 means 512) |
| 188 | 4 | CRC-32 of bytes 0–187 (header version ≥ 4) |
| 192 | 256 | key area: master keys, and in the legacy modes the IV/whitening seed or LRW tweak key at its start |

A header counts as opened only when the magic matches and both CRCs agree. The derivation and the
cipher chain are not stored anywhere. Both are found by trial, as the tools find them:

| Derivation | Iterations | Used by |
| --- | --- | --- |
| PBKDF2-RIPEMD-160 | 2000 / 1000 | TrueCrypt |
| PBKDF2-SHA-512, -Whirlpool | 1000 | TrueCrypt |
| PBKDF2-SHA-1 | 2000 | TrueCrypt < 5 |
| PBKDF2-SHA-512, -Whirlpool, -SHA-256, -BLAKE2s-256, -Streebog-512 | 500 000, or 15 000 + 1000 × PIM | VeraCrypt |
| PBKDF2-RIPEMD-160 | 655 331, or 15 000 + 1000 × PIM | VeraCrypt |
| Argon2id | t = 6, m = 416 MiB, p = 1; with a PIM ≤ 31: t = (PIM − 1)/3 + 3, m = 64 + 32 × (PIM − 1) MiB; above: t = PIM − 18, m = 1 GiB | VeraCrypt 1.26.27+ |

**Keyfiles.** Each keyfile (its first 1 MiB) feeds a running CRC-32. After every byte, the four
CRC bytes, most significant first, are added into a 64-byte pool, or a 128-byte pool for a
VeraCrypt password longer than 64 bytes. The password is then added byte by byte. With keyfiles,
the KDF sees the whole pool; without them, only the password.

## Cipher chains

Ciphers in a chain are listed in encryption order. VeraCrypt names them from the outermost layer
inwards, so its AES(Twofish(Serpent)) is `serpent-twofish-aes` here. Every layer uses the same
sector numbering.

**XTS** (TrueCrypt 5+, VeraCrypt) covers AES, Serpent, Twofish, Camellia, Kuznyechik and the
cascades AES-Twofish, AES-Twofish-Serpent, Serpent-AES, Serpent-Twofish-AES, Twofish-Serpent,
Camellia-Kuznyechik, Camellia-Serpent, Kuznyechik-AES, Kuznyechik-Serpent-Camellia and
Kuznyechik-Twofish. Each cipher takes a 32-byte key and a 32-byte tweak key from the key area,
and the data unit is the 512-byte sector numbered from the container start (data offset / 512).

**LRW** (TrueCrypt 4.1–4.3) uses the first 16 bytes of the key area as the tweak key K2, with
cipher keys from byte 32. Block *j* of data sector *s* (counted from the data area) has index
`s × 32 + j + 1` and is encrypted as `E(P ⊕ T) ⊕ T`, where `T = K2 ⊗ index` in GF(2¹²⁸) modulo
x¹²⁸ + x⁷ + x² + x + 1, with big-endian bit order. Blowfish, CAST5 and Triple DES use GF(2⁶⁴)
modulo x⁶⁴ + x⁴ + x³ + x + 1, an 8-byte K2 and index `s × 64 + j + 1`. cryptsetup does not offer
this 64-bit variant.

**CBC** (TrueCrypt ≤ 4.3) uses the first 32 bytes of the key area as the IV seed, with cipher keys
from byte 32. For a sector numbered *n* from the container start:

- The IV is the seed's first *b* bytes (*b* = block size) XOR the little-endian *n*, repeated.
- The whitening is computed from the 16 seed bytes after those *b*: XOR them with *n* twice, replace
  each 32-bit quarter by its CRC-32 (reflected, seed 0, no final inversion, stored little-endian),
  then fold the halves together, giving `q0⊕q3 ‖ q1⊕q2`.
- Each block is encrypted in CBC and then XORed with the 8-byte whitening. Chaining runs on the
  unwhitened ciphertext.

The cascades come in two kinds:

- **Outer-CBC** (Twofish-AES, Serpent-Twofish-AES, AES-Serpent, AES-Twofish-Serpent,
  Serpent-Twofish) runs the cascade as one composite block cipher.
- **Inner-CBC** (Blowfish-AES, Serpent-Blowfish-AES) runs each cipher's own CBC pass in turn, with
  the IV and whitening for that cipher's block size.

Single ciphers are AES, Serpent, Twofish, Blowfish (TrueCrypt's little-endian word order), CAST5
and Triple DES.

The header in CBC mode is encrypted the same way, with the header key's first bytes as the IV and
its bytes 8–15 as an 8-byte whitening.

## Opening

The standard header is tried at 0, then at the backup (end − 128 KiB). The hidden header is tried at
64 KiB, at the TrueCrypt < 6 position (end − 1536), then at its backup (end − 64 KiB). Where the
data lives follows cryptsetup's `TCRYPT_get_data_offset`/`_get_iv_offset`.

The data area becomes a block device of 512-byte sectors, so the filesystem inside mounts
read-only or read-write; headers are never written. Mount parameters:

| Parameter | Meaning |
| --- | --- |
| `Pim` | VeraCrypt PIM; restricts the search to VeraCrypt derivations |
| `Prf` | try only this PRF |
| `Cipher` | try only chains whose name contains this |
| `Hidden` | `true`: only the hidden header, `false`: only the standard one; unset tries the standard header, then the hidden one, as VeraCrypt does |
| `Keyfiles` | keyfile paths separated by `\|` or newlines |
| `ProtectHidden`, `ProtectHiddenPim`, `ProtectHiddenKeyfiles` | protect the hidden volume while the outer one is mounted (see below) |

## Creating

`TcVolumeWriter` writes the TrueCrypt 5+/VeraCrypt layout above. It supports:

- **format:** VeraCrypt (`VERA`, header version 5, minimum program 1.11) or TrueCrypt 7.1 (`TRUE`,
  minimum program 7.0);
- **cipher:** any XTS chain;
- **derivation:** any VeraCrypt PRF with an optional PIM, or the three TrueCrypt PRFs;
- **keyfiles.**

The key area is 256 random bytes. The backup header gets its own salt. The empty hidden-header
slots are filled with random bytes, so nothing reveals whether a hidden volume exists.

A data area larger than the payload is filled with random bytes. With `Sparse` it is left
unwritten instead, and a seekable file gets holes. Like a VeraCrypt dynamic volume, a sparse
container shows which parts are in use.

## Hidden volumes

A hidden volume lives in the outer volume's free space. Its header uses the spare slot at 64 KiB
(backup 64 KiB before the end), so without its password it looks like the random bytes that fill
that slot in every container.

Placement and header fields follow VeraCrypt 1.26, measured from containers its CLI made:

- The hidden data ends 4096 bytes before the backup header area, so it starts at
  `container − 128 KiB − 4 KiB − size`.
- Both size fields hold the hidden size, `data offset` is that absolute start, and XTS data units
  count from it.
- The outer header records nothing about the hidden volume (hidden size 0).

**Creating.** The hidden data must land only on space the outer volume does not use, so it is
written only after the outer filesystem proves that space free. With the outer password the outer
volume is decrypted, its filesystem detected, and its allocation map (`IFilesystemExtentMap`: FAT,
ext, …) walked; the hidden area must start beyond the last allocated byte. An outer volume
without such a filesystem is refused when the hidden volume is added later. When outer and hidden
volumes are created together, the outer image's own length is the fallback bound. The unused part
of the hidden area is random. A hidden volume cannot be sparse, because holes would reveal it.

**Protection.** When the outer volume is mounted with a `ProtectHidden` password (plus optional
`ProtectHiddenPim` and `ProtectHiddenKeyfiles`), the hidden header is opened too and every write
that would reach the hidden data is refused. After the first refusal, the outer volume refuses
all writes until it is reopened, as VeraCrypt's protection does, so the outer filesystem cannot
carry on half-written. A protection password that opens no hidden header is rejected.

## Not covered

- System encryption: the header at 31 744 in the boot track, and whole-drive/partition mapping.
  The test images include such headers, but nothing here opens them.
- Sector sizes other than 512 bytes.
- Creating the legacy LRW/CBC modes. They are opened, but neither tool writes them any more.

Throughput on the sector path, measured single-threaded in this repository's build: AES-XTS about
290 MiB/s, Twofish 58, Camellia 50, Serpent 18, Kuznyechik 13 MiB/s.
