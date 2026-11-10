# BitLocker — on-disk notes

`FileFormat.BitLocker` opens, mounts and creates BitLocker volumes (Windows' BDE; cryptsetup calls
it BITLK), BitLocker To Go included. The decrypted view is exactly what cryptsetup's `bitlkOpen`
maps, so whole-volume checksums match.

## Sources

| Part | Source | Rung |
| --- | --- | --- |
| Boot sector, FVE metadata block and headers, entry and value codes, VMK/FVEK/validation datums, protection and method tables, key stretch, recovery password, .BEK files, clear key, metadata validation, segment layout, activation checks | cryptsetup `lib/bitlk/bitlk.c`, `bitlk.h` (LGPL-2.1-or-later), converted — see `THIRD-PARTY-NOTICE.cryptsetup.txt` beside the code | 1 |
| AES-CCM key datums | NIST SP 800-38C through .NET's `AesCcm` (12-byte nonce, 16-byte tag, no associated data, as cryptsetup's backends use it) | 3 |
| AES-XTS | IEEE 1619; tweak expansion cross-checked with the repository owner's own `Cipher/algorithms/modes/xts.js` | 3 |
| AES-CBC IV, sector key, diffusers A and B | Niels Ferguson, *AES-CBC + Elephant diffuser: A Disk Encryption Algorithm for Windows Vista*, Microsoft, August 2006, §4.2–4.4 | 3 |
| Fields cryptsetup does not interpret, but which our writer fills the way Windows does (datum method codes, stretch-key method codes, nonce counter, BPB values) | Measured in the Windows-made cryptsetup test images | — |

The Linux kernel's dm-crypt `elephant` IV generator is GPL. It was not used. The diffuser
follows the paper's recurrences directly, and its rotation constants and cycle counts are
copied from §4.4.

The oracles, all kept as tests:

- **cryptsetup's BITLK test images** (`tests/bitlk-images.tar.xz`), made by Windows Vista to 11.
  For each image, `images.conf` gives the passwords, the GUIDs, the cipher, the filesystem UUID
  and the SHA-256 of cryptsetup's whole decrypted mapping. Our decrypted device matches that
  checksum and the UUID for every image and for every credential the file lists
  (`BitlkImageConformanceTests`; the table is below).
- **Sectors and key datums Windows wrote**, cut out of those images into
  `Compression.Tests/BitLocker/ReferenceVectors/`. The required test tier uses them to check the
  key stretch, AES-CCM and all three sector transforms in both directions, without the images.
- **cryptsetup on the volumes we create** (`BitLockerExternalConformanceTests`). `bitlkDump`
  parses them, the user password and the recovery password both pass `--test-passphrase`, and
  the volume key `--dump-volume-key` recovers equals our FVEK. `bitlkOpen` maps the volume to our
  input followed by zeros. Through that mapping, the kernel's FAT driver and our read-write mount
  each read the files the other wrote.

| Image | Password | Recovery password | Other |
| --- | --- | --- | --- |
| aes-xts-128, -256, -128-4k, -crc, -new-entry, -unicode (`anaconda£`), -first-recovery | ✅ | ✅ | |
| aes-xts-128-two-recovery | ✅ | ✅ | second recovery password ✅ |
| aes-xts-128-startup-key, -startup-key-win11 | — (none given) | ✅ | .BEK startup key ✅ |
| aes-xts-128-smart-card | — (smart card) | ✅ | |
| aes-xts-128-clearkey-only | — | — | clear key ✅ |
| aes-cbc-128, -256, -128-4k | ✅ | ✅ | |
| aes-cbc-elephant-128, -256 | ✅ | ✅ | |
| togo-aes-xts-128, togo-aes-cbc-128 | ✅ | ✅ | |
| aes-xts-128-eow, partially-encrypted-aes-cbc-128 | opens | opens | refused, as cryptsetup refuses them (below) |

## Volume layout

| Offset | Content |
| --- | --- |
| 0 | boot sector: `EB 58 90`, then the OEM name `-FVE-FS-`. A To Go volume instead has a FAT boot sector with OEM name `MSWIN4.1`. |
| 11 | bytes per sector (0 means 512; only 512 and 4096 are accepted) |
| 160 (To Go: 424) | volume-type GUID (16 bytes), then three little-endian 64-bit offsets of the FVE metadata copies |
| metadata offset *n* | a 64 KiB FVE metadata area: the metadata block, then its validation block |
| volume-header offset | the volume's original first sectors (its real boot sector), encrypted with IVs for this location |

The `EB 52 90` boot code marks BitLocker version 1 (the Vista beta), which is refused.

The type GUID tells a normal volume (`3bd66749-…`, bytes `3b d6 67 49 29 2e d8 4a 83 99 f6 a3 39
e3 d0 01`) from an encrypt-on-write one (`3b 4d a8 92 80 dd 0e 4d 9e 4e b1 e3 28 4e ae d8`).

**To Go detection.** A To Go volume's boot sector is a valid FAT boot sector, and `MSWIN4.1` is the
OEM name of countless plain FAT volumes. The descriptor therefore matches To Go only when one of
the two BitLocker GUIDs also sits at 424, a signature masked to those 24 bytes. That location
holds boot code on a plain FAT volume, so the match is safe.

### FVE metadata block

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 8 | `-FVE-FS-` |
| 8 | 2 | block size in 16-byte units (at most 64 KiB) |
| 10 | 2 | version: must be 2 |
| 12, 14 | 2 + 2 | current and next state; 4 is "normal" |
| 16 | 8 | volume size in bytes |
| 24 | 4 | unknown (0) |
| 28 | 4 | volume-header size in sectors |
| 32 | 3 × 8 | offsets of the three metadata copies |
| 56 | 8 | volume-header offset |
| 64 | 4 | metadata size (this 48-byte header plus the entries) |
| 68, 72, 76 | 4 each | metadata version (1), header size (48), metadata size again |
| 80 | 16 | volume GUID (mixed-endian, as .NET's `Guid` and libuuid's swapped form print it) |
| 96 | 4 | next nonce counter |
| 100 | 2 | encryption method |
| 102 | 2 | unknown (Windows repeats the method or writes 0) |
| 104 | 8 | creation time, FILETIME |
| 112 | … | entries |

The **validation block** follows the metadata block. Its fields are a 16-bit size (Windows fills
it with the rest of the 64 KiB area), a version of at most 2, and the CRC-32 of the metadata
block. Then comes a nested datum (size 80, type 0, value 5), which is an AES-CCM key datum
sealed with the VMK. Inside it is SHA-256 of the metadata block, with hash type 0x2005.

A copy counts as valid when its signature, version and size check out and its CRC-32 matches;
the first valid copy wins. It must sit at the offset it names for itself. Its three offsets then
replace the boot sector's. The SHA-256 is checked only once a VMK is open. A mismatch there means
tampered metadata, and the volume is refused.

### Entries

Every entry starts with `size, type, value, version` (16 bits each). The **type** is the role:
0 property, 2 VMK, 3 FVEK, 6 startup key, 7 description, 15 volume header, 0x19 volume GUID. The
**value** is the payload kind: 1 key, 2 UTF-16 string, 3 stretch key, 4 use key, 5 AES-CCM
encrypted key, 6 TPM key, 7 validation, 8 VMK, 9 external key, 15 offset+size, 0x15 recovery
time, 0x17 GUID, 0x18 hint.

- **VMK** (type 2, value 8): the protector GUID (16), modification FILETIME (8), unknown (2),
  protection (2: 0x0000 clear key, 0x0100 TPM, 0x0200 startup key, 0x0500 TPM+PIN, 0x0800 recovery
  password, 0x1000 smart card, 0x2000 password). Nested properties follow:
  - a stretch key (value 3): a 4-byte method, then the 16-byte salt;
  - the wrapped VMK (value 5): nonce (12), tag (16), ciphertext;
  - a clear key (value 1, clear-key protectors only), whose 4-byte method is followed by the key
    that unwraps the VMK;
  - optionally a name (value 2), recovery time (0x15) or hint (0x18).

  A password, recovery-password, startup-key or clear-key protector carrying any other property
  makes the volume unreadable, as in cryptsetup. Other protectors are only listed.
- **FVEK** (type 3, value 5): nonce, tag and ciphertext, sealed with the VMK.
- **Volume header** (type 15, value 15): 64-bit offset and 64-bit size of the relocated header.
- **Description** (type 7, value 2): UTF-16, NUL-terminated.

A **key datum**, once decrypted, holds `size, 0, 1 (key), 0` (16 bits each), then a 32-bit method,
then the key. The size must equal the ciphertext length. Windows writes method 0x2003 for a VMK,
the encryption method for the FVEK, and 0x2005 for the validation hash.

### Encryption methods

| Code | cryptsetup name | FVEK as stored | Key dm-crypt takes |
| --- | --- | --- | --- |
| 0x8000 | aes-cbc-elephant, 256 bits | 64 bytes: CBC key, 16 unused, diffuser key, 16 unused | CBC key ‖ diffuser key (32) |
| 0x8001 | aes-cbc-elephant, 512 bits | CBC key (32) ‖ diffuser key (32) | as stored |
| 0x8002 / 0x8003 | aes-cbc-eboiv, 128 / 256 | the AES key | as stored |
| 0x8004 / 0x8005 | aes-xts-plain64, 256 / 512 | data key ‖ tweak key | as stored |

## Opening

The protectors are tried in metadata order:

- **Password:** SHA-256(SHA-256(UTF-16LE password)), then stretched.
- **Recovery password:** eight groups of six digits joined by `-`. Each group is divisible by 11,
  and each quotient below 65536 forms 16 bits of a 16-byte key, little-endian. The key is
  SHA-256 hashed, then stretched. A password with this shape is also tried as a recovery
  password, as cryptsetup tries it.
- **The stretch:** an 88-byte state (last hash 32, initial hash 32, salt 16, counter 8 LE) is
  hashed with SHA-256 0x100000 times. Each hash replaces "last", and the counter counts up.
  "Last" is the AES-256 key that unwraps the VMK.
- **Startup key (.BEK):** a 48-byte header names the protector GUID (at 16) and must have
  version 1 and a size equal to the file length. One entry follows (type 6, value 9) with a
  24-byte header. Its nested key (value 1) is the AES-256 key that unwraps the VMK. A nested GUID
  (value 0x17) must equal the volume GUID.
- **Clear key:** the protector's own clear key unwraps the VMK, with no credentials at all.

An open VMK must decrypt the validation datum, and the hash inside must match. Only then is the
FVEK unwrapped. A missing credential raises `ArchivePasswordException` with
`PasswordWasSupplied = false`. A wrong one raises it with `true`.

## Sector transforms

`s` is the IV sector number, counted in units of the sector size, as dm-crypt counts it with
`iv_large_sectors`. `e(s)` is the 16-byte block holding the byte offset `s × sector size`
little-endian in its first 8 bytes.

- **AES-XTS** (IEEE 1619): the tweak is `s` little-endian (`plain64`).
- **AES-CBC** (Ferguson §4.2, the "EBOIV" IV): `IV = AES(K, e(s))`, using the CBC key itself.
- **AES-CBC + Elephant** (Ferguson §4.2–4.4). To encrypt:
  1. XOR the sector with the sector key `K_s = AES(K_sec, e(s)) ‖ AES(K_sec, e'(s))`, repeated,
     where `e'(s)` is `e(s)` with its last byte set to 128.
  2. Run diffuser A, then diffuser B, in their encryption directions.
  3. Encrypt with AES-CBC under the IV above.

  Decryption runs the inverse in reverse order. Both diffusers treat the sector as
  little-endian 32-bit words `d_0 … d_{n−1}`, with indices taken mod n:
  - A decrypts with `d_i ← d_i + (d_{i−2} ⊕ (d_{i−5} ⋘ R_a[i mod 4]))` for
    `i = 0 … 5n−1`, where `R_a = [9, 0, 13, 0]`.
  - B decrypts with `d_i ← d_i + (d_{i+2} ⊕ (d_{i+5} ⋘ R_b[i mod 4]))` for
    `i = 0 … 3n−1`, where `R_b = [0, 10, 0, 25]`.
  - Encryption runs each loop backwards and subtracts.

  The paper publishes no test vectors. Windows' own Elephant sectors in the reference vectors
  serve as known answers, and a test also checks the optimised loops against the literal
  recurrences.

## The decrypted view

The view reproduces `BITLK_activate_by_volume_key`. Positions are in 512-byte units, and the
device is the image length rounded down to 512 bytes.

1. Zero segments go over the three 64 KiB metadata areas and over the volume-header location.
2. A crypt segment maps view sectors `0 … header size` to the volume-header location, with IVs
   counted from there.
3. Every remaining gap becomes a crypt segment at its own position, with IVs equal to that
   position. The gaps are found by cryptsetup's own scan, from the end of the header segment.
   If that scan cannot cover the device, or needs more than 10 segments, the volume is refused,
   as cryptsetup refuses it. The layout must therefore end in a crypt segment.
4. With 4096-byte sectors, every segment must be 4 KiB aligned. IVs are then the 512-byte
   position divided by 8.

Zero segments read as zeros. A write to one is discarded, as dm-zero discards it, so the
metadata is never written. Crypt segments decrypt on read and re-encrypt on write. TRIM is
refused.

**Refused, as cryptsetup refuses them.** A volume whose state is not 4/4, or whose type GUID is
encrypt-on-write or unknown, can be unlocked, but it is not activated. Both such images in
cryptsetup's set (`aes-xts-128-eow` and `partially-encrypted-aes-cbc-128`) carry the
encrypt-on-write GUID. A part of such a volume is still plaintext, and nothing on disk that
cryptsetup interprets records which part. So there is no reference view to reproduce, and
`images.conf` gives no checksum for them. The conformance test checks that their keys open and
that activation is refused, then reports itself skipped with that reason.

## What is written

`BitLockerWriter` encrypts a volume image (whole 512-byte sectors, more than 8 KiB) so that
cryptsetup's view of the result is the image followed by zeros:

| Offset | Content |
| --- | --- |
| 0 | `-FVE-FS-` boot sector: Windows' BPB values (512-byte sectors, media F8, 63 × 255 geometry, the FAT32 extension fields), no boot code, the normal-volume GUID and the metadata offsets at 160, `55 AA` |
| 512 … 8 KiB | zeros (the image's own first 8 KiB live in the header backup) |
| 8 KiB … image end | the image, encrypted in place |
| … 64 KiB boundary | encrypted zeros |
| A | metadata copy 0 |
| A + 64 KiB | header backup: the image's first 8 KiB, encrypted with IVs for this location |
| A + 72 KiB | metadata copy 1 |
| A + 136 KiB | metadata copy 2 |
| A + 200 KiB … +64 KiB | encrypted zeros (cryptsetup's scan needs a crypt segment at the end) |

Everything BitLocker adds lies past the end of the image. Windows places these areas inside
NTFS and marks them as allocated there. An arbitrary filesystem image has no such marking. Inside
the image, the areas would be free sectors that read as zeros and silently drop writes.

The metadata holds:

- a description;
- a password protector (0x2000, stretch method 0x1001), unless only a recovery password is
  wanted;
- a recovery-password protector (0x0800, stretch method 0x1000);
- the FVEK sealed with a random VMK;
- the volume-header entry;
- the validation datum.

Nonces are the creation FILETIME followed by a counter, and the header records the next counter
value. The default method is AES-XTS-128. AES-XTS-256, AES-CBC-128/256 and
AES-CBC-128/256 + Elephant can also be chosen.

The recovery password is taken as an option, or generated by `BitLockerWriter` and returned in
its result. The format descriptor cannot hand a generated secret back, so without a
`RecoveryPassword` option it writes a password protector only rather than a recovery protector
nobody holds.

**Verified:** cryptsetup 2.8 reads what we write. `bitlkDump` parses it, both passwords open it,
`--dump-volume-key` returns our FVEK, and the kernel's dm-crypt decrypts every crypt segment to
our input. The full `bitlkOpen` mapping, and the kernel FAT round trip through it, need the
dm-zero target, and they run in CI's "BitLocker oracle" step.

**Not verified: Windows.** No Windows host is available to these tests, so nobody has checked
whether Windows unlocks a volume we wrote. Several things here differ from what Windows writes.
The VMK entries omit the nested datums Windows keeps inside the stretch-key property, whose
meaning nobody has documented. The volume-header entry carries only offset and size, where
Windows writes 100 bytes. There is no boot code. The metadata lies outside the filesystem rather
than in files the filesystem knows. Expect Windows to refuse these volumes until someone tests
them.

## Not covered

- TPM, TPM+PIN and smart-card protectors are listed, not opened (cryptsetup cannot open them
  either).
- Encrypt-on-write and mid-conversion volumes are not activated (see above).
- BitLocker version 1 (Vista beta) is refused.
- Creating To Go volumes, startup keys or clear-key (suspended) volumes is not supported.
- The FAT reader recurses without end into the corrupt directory clusters of the To Go test
  images. Their unused sectors were zeroed and decrypt to noise, so the FAT tables are mostly
  garbage, as `fsck.vfat` confirms on cryptsetup's own view. The To Go images are therefore
  checked by checksum and FAT serial, not by mounting.
