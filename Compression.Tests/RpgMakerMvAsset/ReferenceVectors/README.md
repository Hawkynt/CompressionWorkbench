# RPG Maker MV/MZ asset reference vectors

| File | Origin |
| --- | --- |
| `tile.png`, `tone.ogg`, `tone.m4a` | generated with FFmpeg 8.1.2 (`testsrc` 16×16 frame; 0.3 s 440 Hz `sine` as Vorbis `-q:a 0` and AAC 32 kb/s); no third-party content. |
| `tile.rpgmvp`, `tone.rpgmvo`, `tone.rpgmvm` | the three media files encrypted by Petschko's RPG-Maker-MV-Decrypter (`scripts/Decrypter.js`, revision `d2ae9b8c2c87d51eb5643180efb74bddf611e829`) with key `1a2b3c4d5e6f708192a3b4c5d6e7f809`, driven by `oracle.js`. The same tool restores the PNG without the key and decrypts the Ogg back to the original. |
| `Decrypter.js`, `ErrorException.js` | unmodified from that revision; MIT, © 2016 Peter Dragicevic (`LICENCE-Petschko.txt`). |
| `oracle.js` | a Node.js driver that loads those two scripts and runs `encrypt`, `decrypt` or `restorePngHeader` on a file. |

Our encryption must reproduce the three assets byte for byte, and Petschko's `decrypt` must turn assets
we write back into the originals.
