# SPIFFS reference images

Images written by SPIFFS' own image tools, checked in so the reader tests run without
them. `generate.sh` documents how they were made; it is not a build input.

| File | Made by | Geometry |
|------|---------|----------|
| `mkspiffs-esp8266.spiffs.gz` | mkspiffs f248296 (SPIFFS f5e26c4), Arduino ESP8266 build: `-DSPIFFS_USE_MAGIC_LENGTH=0 -DSPIFFS_ALIGNED_OBJECT_INDEX_TABLES=1`; `-p 256 -b 8192 -s 131072` | 256-byte pages, 8 KiB blocks, no metadata, aligned index tables, plain magic |
| `mkspiffs-generic.spiffs.gz` | the same mkspiffs, generic build (SPIFFS defaults); `-p 256 -b 4096 -s 131072` | 256/4096, no metadata, unaligned, length-folded magic |
| `mkspiffs-esp32.spiffs.gz` | the same mkspiffs, Arduino ESP32 build: `-DSPIFFS_OBJ_META_LEN=4`; `-p 256 -b 4096 -s 131072` | 256/4096, 4-byte metadata, unaligned, length-folded magic |
| `spiffsgen.spiffs.gz` | ESP-IDF v5.2.2 `components/spiffs/spiffsgen.py` with its defaults, image size 131072 | 256/4096, 4-byte metadata, unaligned, length-folded magic |
| `manifest.txt` | `generate.sh` | SHA-256 and name of every file in the source tree |

The tree holds an empty file, a file of exactly one data page (251 bytes), one of
252 bytes (two pages), a 40 000-byte file whose index outgrows the object index header
page, a nested name and a name of the maximum 31 bytes (with its leading slash).
Each image was listed back with `mkspiffs -l` from the build that made it.

Captured 2026-10-30 on Ubuntu 24.04 under WSL2. Compressed with `gzip -9 -n`.
