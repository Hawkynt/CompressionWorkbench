# OpenPGP test vectors

Made with GnuPG 2.4.4 (Ubuntu 24.04) in a throw-away `GNUPGHOME`. They are test material only:
the keys exist for these tests, and their passphrase is `keypass`.

| File | Command |
| --- | --- |
| `ecc-secret.asc` | `gpg --quick-gen-key 'CWB ECC <cwb-ecc@test.invalid>' default default never` (ed25519 primary, cv25519 encryption subkey), exported with `--armor --export-secret-keys` |
| `ecc-public.asc` | the same key, `--armor --export` |
| `rsa-secret.asc` | `gpg --quick-gen-key 'CWB RSA <cwb-rsa@test.invalid>' rsa2048 default never` plus `--quick-add-key <fpr> rsa2048 encr never`, exported with `--armor --export-secret-keys` |
| `ecc-seipd.pgp` | `gpg -r cwb-ecc@test.invalid --rfc4880 -e plain.txt`: ECDH session key, tag 18 (CFB + MDC), ZLIB-compressed literal |
| `rsa-ocb.pgp` | `gpg -r cwb-rsa@test.invalid -e plain.txt`: RSA session key, tag 20 AEAD (AES-256, OCB) |
| `rsa-seipd-bz2.pgp` | `gpg -r cwb-rsa@test.invalid --compress-algo bzip2 --rfc4880 -e plain.txt`: tag 18, BZip2-compressed literal |

`plain.txt` was `hello openpgp\n`. `gpg --list-packets` shows the structure of each message.
