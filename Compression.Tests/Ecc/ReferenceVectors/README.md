# dvdisaster ecc reference vector

| File | Origin |
| --- | --- |
| `tiny.ecc` | written on 2026-10-30 in WSL (Ubuntu 24.04) by dvdisaster 0.79.10: `dvdisaster -i tiny.iso -e tiny.ecc -c -n 8` (RS01, 8 roots), for a 40-sector (81920-byte) image whose byte i is `(i*7) ^ (i >> 11)`. |

`dvdisaster -i tiny.iso -e tiny.ecc -t` reports: created by dvdisaster-0.79.10, requires dvdisaster-0.55,
40 medium sectors, image md5sum `fc0a12726edaa590185c0b3488e17e82` (good), fingerprint match good.
