# StuffIt X method-7 (JPEG) stream vectors

Each `<name>.jc` is a complete method-7 stream and `<name>.jpg` the JPEG file it restores to. They
are the eight `cases` of `Tests/Fixtures/stuffit/slice7-jpeg-vectors.json` in KaitoKit
(https://github.com/shunnag/KaitoKit), revision `bb14fb23753b57386f72caf64887cabb8d9fcaf2`,
MIT licence (see `THIRD-PARTY-NOTICE.KaitoKit.txt` next to the StuffIt X sources). The bytes were
copied unchanged from the hex strings.

| Name | Mode | Frame | Covers |
|---|---|---|---|
| `mode2-prog0-1x1-rst0-literal0-inter0` | 2 | SOF0 | one block; fill bytes and a COM segment after the scan |
| `mode2-prog0-3x3-rst2-literal0-inter0` | 2 | SOF0 | 3×3 MCUs, a restart every two |
| `mode1-prog0-3x3-rst2-literal0-inter0` | 1 | SOF0 | three components, 3×3 MCUs, restarts |
| `mode2-prog1-1x1-rst0-literal0-inter0` | 2 | SOF2 | one progressive block |
| `mode2-prog1-2x3-rst2-literal0-inter0` | 2 | SOF2 | progressive scans, restarts |
| `mode2-prog0-1x1-rst0-literal1-inter0` | 2 | none | `FF D8 41 FF FF 42 FF D9`: literal bytes and no frame |
| `mode1-prog0-1x1-rst0-literal1-inter0` | 1 | none | the same bytes under mode 1 |
| `mode2-prog1-2x3-rst0-literal0-inter1` | 2 | SOF2 | progressive scans with segments between them |

The second WZ integer of these streams (the declared size) is 2^64 − 1. Only mode 0 uses it.
