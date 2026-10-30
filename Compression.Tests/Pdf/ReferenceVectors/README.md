# Encrypted PDF reference vectors

Written by pypdf 6.21.2 (BSD-3-Clause, Python 3.13, Windows 11) with `make_pdfs.py` (kept here as
documentation): one blank 72×72 page whose resources hold a 2×2 RGB image XObject (FlateDecode,
pixels red, green, blue, white) and one attachment `hello.txt` = `attached to an encrypted PDF\n`.
pypdf read every file back with the stated password and got the same pixels and attachment.

| File | pypdf `encrypt(algorithm=…)` | /V /R | Passwords |
| --- | --- | --- | --- |
| `pypdf-rc4-40.pdf` | `RC4-40` | 1 / 2 | user empty, owner `owner` |
| `pypdf-rc4-128.pdf` | `RC4-128` | 2 / 3 | user empty, owner `owner` |
| `pypdf-aes-128.pdf` | `AES-128` (AESV2 crypt filter) | 4 / 4 | user empty, owner `owner` |
| `pypdf-aes-256-r5.pdf` | `AES-256-R5` (AESV3) | 5 / 5 | user empty, owner `owner` |
| `pypdf-aes-256.pdf` | `AES-256` (AESV3) | 5 / 6 | user empty, owner `owner` |
| `pypdf-rc4-128-userpw.pdf` | `RC4-128` | 2 / 3 | user `user`, owner `owner` |
