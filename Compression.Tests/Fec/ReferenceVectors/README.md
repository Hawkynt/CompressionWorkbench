# lziprecover fec reference vectors

Written on 2026-10-30 in WSL (Ubuntu 24.04) by lziprecover 1.25, built from
`https://download.savannah.gnu.org/releases/lzip/lziprecover/lziprecover-1.25.tar.gz` (Ubuntu's 1.24
predates fec files), for a 5000-byte file whose byte i is `(i*13 + (i >> 8)) mod 256` (MD5
`77441b2e36f615b42cc509c78d0cb208`):

| File | Command | `lziprecover -Fl -v` |
| --- | --- | --- |
| `sample-gf8.fec` | `lziprecover -Fc -b 512 sample.bin` (renamed from sample.bin.fec) | block size 512, data blocks 10, fec blocks 8 (numbers 0-7), GF(2^8) CRC32 CRC32-C |
| `sample-gf16.fec` | `lziprecover -Fc3 --gf16 sample16.bin` (renamed from sample16.bin.fec) | block size 512, data blocks 10, fec blocks 3 (numbers 0-2), GF(2^16) CRC32 CRC32-C |
