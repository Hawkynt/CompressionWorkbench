# LZMA2 reference vectors

| File | Produced by | Input | Why it is here |
| --- | --- | --- | --- |
| `lzma2-copy-then-continue.xz` | 7-Zip 26.04 (x64), `7z a -txz -mx1` | 4,000 bytes of word text, 100,000 bytes of Python `random.Random(7)` noise, 4,000 bytes of word text (108,000 bytes, SHA-256 `73904a71b348a61232473c72824c366c58d2dfdf898849040453d3478a80a002`) | Its LZMA2 chunks are `0xE0, 0x02, 0x80`: an incompressible stretch stored as a continuing copy chunk, then an LZMA chunk that resets nothing and carries the coder state on from before the copy. |
