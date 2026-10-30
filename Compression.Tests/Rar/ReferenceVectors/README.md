# RAR encryption reference vectors

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
