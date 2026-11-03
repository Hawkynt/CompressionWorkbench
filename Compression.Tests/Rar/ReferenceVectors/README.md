# RAR reference vectors

Archives in the RAR 4 format (compression version 29) that real RAR encoders wrote. Every
file in them carries its CRC-32, so a reader that gets one bit wrong fails the check.

## `rar624-*.rar`

Written on 2026-10-26 by RAR 6.24 for Linux x64 (`rarlinux-x64-624.tar.gz` from rarlab.com,
trial version), the last RAR release that still writes RAR 4 archives. The inputs come from
`make_inputs.py` (deterministic, fixed seed). The script and the commands are below.
`unrar t` (UNRAR 7.00) tests every archive OK.

```sh
A="a -ma4 -ep -idq -ts-"
rar $A -m3 -mct- -mcE- -mcD- -mcA- -mcC- -mcI- lz.rar text.txt
rar $A -m5 -mct- -md4m best.rar text.txt table.bin
rar $A -s -m3 -mct- solid.rar text.txt x86.bin table.bin
rar $A -m3 -mct- -mcE+ e8.rar x86.bin
rar $A -m3 -mct- -mcD+ delta.rar table.bin
rar $A -m3 -mct- -mcA+ audio.rar audio.wav
rar $A -m3 -mct- -mcC+ rgb.rar image.bmp
rar $A -m3 -mct- -mcI+ itanium.rar ia64.bin
rar $A -m3 -mct+ ppmd.rar text.txt
```

The encrypted archives use the password `secret`. They were written on 2026-10-30 with
the same RAR 6.24 build:

```sh
A="a -ma4 -ep -idq -ts- -psecret"
rar $A -m0 enc-stored.rar text.txt
rar $A -m3 -mct- enc-lz.rar text.txt table.bin
rar $A -m3 -mct- -s enc-solid.rar text.txt x86.bin
rar a -ma4 -ep -idq -ts- -hpsecret -m3 -mct- enc-headers.rar text.txt
```

## `libarchive-*.rar`

From libarchive's test suite (`libarchive/test/`, tag v3.7.7, BSD-2-Clause), decoded from
their `.uu` form:

- `test_read_format_rar_filter.rar` becomes `libarchive-filter.rar`.
- `test_read_format_rar_compress_best.rar` becomes `libarchive-compress-best.rar`.
- `test_read_format_rar_compress_normal.rar` becomes `libarchive-compress-normal.rar`.

## Encryption vectors (WinRAR 2.90, libarchive)

| File | Origin |
| --- | --- |
| `winrar290-rar20-crypt.rar` | Written by the console `Rar.exe` of WinRAR 2.90 (rarlab.com `wrar290.exe`, sha256 `7c2e3ec5…79f6`, run as an evaluation copy): `Rar a -ppassword winrar290-rar20-crypt.rar a.txt b.bin`. Unpack version 20, method 3, RAR 2.0 cipher. |
| `winrar290-rar20-crypt-store.rar` | Same inputs, `Rar a -m0 -ppassword …`: stored members, each padded to a 16-byte multiple before encryption. |
| `winrar290-plain.rar` | Same WinRAR 2.90, no password: `Rar a winrar290-plain.rar a.txt b.bin c.txt` (a.txt stored, b.bin and c.txt method 3) — tells decoder faults from cipher faults. |
| `winrar290-rar20-crypt-text.rar` | `Rar a -ppassword … a.txt c.txt`: the RAR 2.0 cipher over compressed text. |
| `libarchive-rar4-encrypted.rar` | libarchive's `test_read_format_rar4_encrypted.rar` (BSD-2-Clause): a.txt/c.txt plain stored, b.txt AES with password `password`, d.txt AES with another password. |
| `libarchive-rar3-aes.rar` | libarchive's `test_read_format_rar_encryption_data.rar` (uudecoded from `libarchive/test/*.rar.uu`, master 2026-10-26; BSD-2-Clause), password `12345678`: RAR 2.9 format, AES-128 with salt. |

Inputs of the WinRAR archives: `a.txt` = `hello rar 2.0 encryption\n`; `b.bin` = bytes 0..255 repeated 16 times; `c.txt` = 120 lines `line N: the quick brown fox jumps over the lazy dog\n` (N = 0..119).
7-Zip 26.04 tests every archive here as OK with the stated passwords (d.txt of the libarchive file excepted).
