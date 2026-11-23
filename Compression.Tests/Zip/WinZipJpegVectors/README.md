# ZIP method 96 (WinZip JPEG) reference vectors

Both files come from bwhitn/unpackio (https://github.com/bwhitn/unpackio, revision
`b70a5e23e75c83f045593e2106a26d9f62b2245f`,
`crates/unpackio/tests/fixtures/method96/*.b64`). They are base64-decoded here and
released under that repository's MIT test-fixture terms (`CORPUS.md`, "ZIP and RPM
evidence").

| File | Bytes | SHA-256 | What it is |
|------|------:|---------|------------|
| `winzip21-method96.zipx` | 3,347 | `47454618f65cef060c2ba1d96b8b36d8028681ac693f9be3d57e791ec57c15e1` | Written by WinZip 21.0.12288 (64-bit evaluation edition) with ZIPX "Best method"; one member, method 96, payload at local offset 59 |
| `method96-project-authored.jpg` | 7,823 | `95ef01838a55308006fabf6d2e512123a37916067cce58dd5076c89da43e2244` | The archived JPEG (JFIF 1.01 baseline, 8-bit, 512 × 384, three components, CRC-32 `7422FE59`) |

The archive must extract to the JPEG byte for byte.
