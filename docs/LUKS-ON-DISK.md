# LUKS — on-disk notes

`FileFormat.Luks` is implemented from the published specifications:

- *LUKS1 On-Disk Format Specification* 1.2.3 (Clemens Fruhwirth);
- *LUKS2 On-Disk Format Specification* (cryptsetup project, `docs/on-disk-format-luks2.pdf`);
- IEEE 1619 for AES-XTS, RFC 2898/8018 for PBKDF2, RFC 9106 for Argon2.

`cryptsetup` 2.7 is the oracle in `Compression.Tests/Luks/LuksTests.cs`, in both directions:

- **It reads ours.** It opens our volumes, and its device-mapper view of them is our plaintext.
- **We read its.** It formats volumes and writes data through its own mapping, and our reader
  decrypts that data.

## What is read

| Part | Supported |
| --- | --- |
| Header | LUKS1 (592-byte big-endian header, 8 key slots). LUKS2 (4 KiB binary header, header checksum verified, JSON metadata). |
| Key-slot KDF | PBKDF2 over SHA-1, SHA-256, SHA-384 and SHA-512. Argon2i and Argon2id (LUKS2 `time`/`memory`/`cpus`). |
| Anti-forensic splitter | `luks1` AF, with any stripe count and the hashes above. |
| Ciphers | AES with `xts-plain64`, `xts-plain`, `cbc-plain64`, `cbc-plain` and `cbc-essiv:sha256`. |
| Volume-key digest | PBKDF2 (LUKS1 `mk-digest`, LUKS2 `digests`). |
| Data segment | The first crypt segment: offset, `dynamic` or fixed size, `iv_tweak`, and 512 or 4096-byte sectors. dm-crypt counts the IV in 512-byte units even for larger sectors. |

The passphrase is tried against every active key slot. A volume key counts as found only if
it reproduces the stored digest.

## What is written

The writer creates LUKS1 volumes:

- aes-xts-plain64 with a 512-bit volume key;
- PBKDF2-SHA256, with configurable iterations (minimum 1000, as in cryptsetup);
- key slot 0 active, 4000 stripes;
- key material at sector 8, with slots laid out 4 KiB-aligned the way cryptsetup does;
- the payload at 2 MiB (sector 4096).

The payload must be whole 512-byte sectors.

## Refused

- Ciphers other than AES (Serpent, Twofish, …).
- Hash specs outside SHA-1 and SHA-2 (RIPEMD-160, Whirlpool).
- LUKS2 segments that are not plain crypt segments (online reencryption in progress).
- Detached headers.
- Editing an existing volume. The archive edit API carries no passphrase, so an edit could not
  be re-encrypted under the existing key.
