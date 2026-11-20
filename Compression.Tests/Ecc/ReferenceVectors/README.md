# dvdisaster ecc reference vector

| File | Origin |
| --- | --- |
| `short-rs01.ecc` | written on 2026-11-20 by the same dvdisaster: `dvdisaster -i short.iso -e short-rs01.ecc -c -n 8` (RS01, 8 roots) for the first 10 sectors (20480 bytes) of the image below. With no sector 16, it records no fingerprint. |
| `tiny.ecc` | written on 2026-10-30 in WSL (Ubuntu 24.04) by dvdisaster 0.79.10: `dvdisaster -i tiny.iso -e tiny.ecc -c -n 8` (RS01, 8 roots), for a 40-sector (81920-byte) image whose byte i is `(i*7) ^ (i >> 11)`. |

`dvdisaster -i tiny.iso -e tiny.ecc -t` reports: created by dvdisaster-0.79.10, requires dvdisaster-0.55,
40 medium sectors, image md5sum `fc0a12726edaa590185c0b3488e17e82` (good), fingerprint match good.
