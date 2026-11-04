# Chromium resource pack reference vectors

| File | Origin |
| --- | --- |
| `grit_oracle.py` | `ReadDataPackFromString` and `WriteDataPackToString` copied from Chromium's `tools/grit/grit/format/data_pack.py` (main, fetched 2026-10-26), plus the version 4 writer of revision `1be9fc91a8947739a44e1f2e9eb2cd173b42ef95` (bytes instead of Python 2 `str`). Copyright 2012 The Chromium Authors, BSD-3-Clause (licence text in `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.ChromiumPak/THIRD-PARTY-NOTICE.ChromiumPak.txt`). |
| `grit-v5-utf8.pak` | `python grit_oracle.py make`: version 5, UTF-8, ids 101, 102 (a PNG-headed blob), 103 (same bytes as 101, so grit aliases it), 200 (empty), 4096, 65535. |
| `grit-v5-binary.pak` | same run: version 5, binary, ids 1 and 2. |
| `grit-v5-empty.pak` | same run: version 5, no resources. |
| `grit-v4-utf8.pak` | same run, grit's version 4 writer: ids 7, 8 (empty), 300. |
| `grit-listing.txt` | `python grit_oracle.py list` of the four packs: version, encoding, resource and alias counts, then per resource its length, SHA-256 and alias target. |

Our writer must reproduce every pack byte for byte from the same resources. When an installed Chrome is
found (or `CWB_CHROME_DIR` points at one), the tests also require that reading and rewriting its
`chrome_100_percent.pak` and `en-US.pak` reproduces them byte for byte; on 2026-10-26 that held for
Chrome 155.0.8059.40, whose packs grit's reader also lists (version 5, UTF-8; 449 resources/21 aliases
and 12533/1888 respectively).
