# dm-crypt plain mode — on-disk notes

`FileFormat.DmCrypt` reads, mounts and writes the volumes `cryptsetup open --type plain` maps.
A plain volume has no header: every parameter is supplied when it is opened, and the device holds
nothing but ciphertext.

## Sources

The key derivation is not in any normative document. It was derived from the behaviour of
`cryptsetup` 2.8 (`open --type plain` with a passphrase on standard input), whose implementation
is GPL-licensed and was read for orientation only, not converted. Every rule below is pinned by
`DmCryptPlainExternalConformanceTests`, which runs cryptsetup in both directions. Sector
encryption is shared with LUKS: AES per IEEE 1619 (XTS) and the dm-crypt IV generators.

## Parameters

| Parameter | cryptsetup option | Default | Meaning |
| --- | --- | --- | --- |
| `Cipher` | `-c` | `aes-xts-plain64` | `cipher-mode-iv`: AES with `xts-plain64`, `xts-plain`, `cbc-plain64`, `cbc-plain`, `cbc-essiv:sha256` |
| `Hash` | `-h` | `sha256` | passphrase hash, optionally `hash:bytes`; `plain` uses the passphrase as the key |
| `KeySize` | `-s` | `256` | key size in bits (XTS keys are two AES keys) |
| `Offset` | `--offset` | `0` | data start, in 512-byte sectors |
| `Skip` | `--skip` | `0` | IV sector number of the first data sector |
| `Size` | `--size` | rest of device | data length in 512-byte sectors |

The defaults are cryptsetup 2.8's compiled-in plain-mode defaults (`cryptsetup --help`).

## Key derivation

For a key of *K* bytes and a hash `h` with digest length *D*:

1. The key is filled from successive digests. Round *r* (from 0) hashes *r* ASCII `A` characters
   followed by the passphrase bytes: `h(pass)`, `h("A" + pass)`, `h("AA" + pass)`, …
2. Each digest contributes up to *D* bytes, and the last one is cut to what is left.
3. With `h:n`, only the first *n* bytes are derived this way and the remaining *K − n* are zero.
4. With `plain`, the first *n* (default *K*) passphrase bytes are the key. A shorter passphrase is
   refused.

The passphrase is used exactly as given: no trailing newline, and no character-set conversion
beyond UTF-8.

Supported hashes: `sha1`, `sha256`, `sha384`, `sha512`, `ripemd160`, `whirlpool`.

## Sectors and IVs

Data sector *i* (0-based, 512 bytes, at byte `(Offset + i) × 512`) is encrypted with IV sector
`Skip + i`. The IV generators are the same as LUKS's: `plain` is the low 32 bits of that number,
`plain64` is all 64, little-endian, and `essiv:sha256` is AES-encrypted under SHA-256(key).

## Checks

There is none. A wrong passphrase or wrong parameter decrypts to noise. Mounting then finds no
filesystem, and extracting writes the noise. This is inherent to the format: nothing on disk
reveals that a volume exists.

## What is written

`Create` writes `Offset` sectors of random filler followed by the encrypted payload, using the
same parameters. The payload must be whole 512-byte sectors.
