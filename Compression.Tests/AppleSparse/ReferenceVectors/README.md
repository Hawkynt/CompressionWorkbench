# Apple sparsebundle reference bundles

Both bundles were written by Apple's `hdiutil` on macOS, never by CompressionWorkbench. They are
copied byte for byte from the upstream repositories at the pinned revisions below (fetched
2026-10-01 from `raw.githubusercontent.com`, so no checkout rewrote a line ending; see
`.gitattributes`).

| Bundle | Upstream | Revision | Written by |
| --- | --- | --- | --- |
| `hfsplus.sparsebundle` | [keramics/keramics](https://github.com/keramics/keramics) `test_data/sparsebundle/hfsplus.sparsebundle` | `c0a985640f4a74a294ec95e080099774b2b5f908` | `hdiutil create -fs 'HFS+' -size 4M -type SPARSEBUNDLE -volname hfsplus_test` (`scripts/generate_sparsebundle_test_data_macos.sh`), then files added through the mounted volume |
| `dmg-sparsebundle.sparsebundle` | [abrignoni/ewfprobe](https://github.com/abrignoni/ewfprobe) `tests/fixtures/dmg-sparsebundle.sparsebundle` | `34c34b8496f0f3d57450d77b7f27a3247f4b08aa` | `hdiutil`, macOS 26.6.2, format UDSB (`tests/fixtures/manifest.json`) |

| File | Bytes | SHA-256 |
| --- | --- | --- |
| `hfsplus.sparsebundle/Info.plist`, `Info.bckup` | 494 | `c56a469358ffcc17f1a91acf448fb1bfb0a126085fc7e8b64328b1c2b5270607` |
| `hfsplus.sparsebundle/bands/0` | 4194304 | `cb48bcbc19635f7b8c0ccc58da376eac0a309cc12c83c9e2a942acbaf20418c3` |
| `dmg-sparsebundle.sparsebundle/Info.plist`, `Info.bckup` | 494 | `30421b181f68d7ade88eb680a8b8ff9b63a277f30d21c52f739f26710ac27a4b` |
| `dmg-sparsebundle.sparsebundle/bands/0` | 1048576 | `0ac81bdcc036e8cb08cc04a2c64999f69c63fc755a588df624b2335e94ac55ad` |
| `dmg-sparsebundle.sparsebundle/bands/1` | 1048576 | `4f12b1cad80d8bdb498f1a5d6fb0f996e12bde6343834f28bcb2ac0bfe0894db` |
| `dmg-sparsebundle.sparsebundle/bands/2` | 1048576 | `30e14955ebf1352266dc2ff8067e68104607e750abb9d3b36582b8af909fcb58` |

`token` and `lock` are empty in both bundles.

## Expected values and where they come from

| Bundle | Media | Source of the expected value |
| --- | --- | --- |
| `hfsplus.sparsebundle` | 4194304 bytes, MD5 `7adf013daec71e509669a9315a6a173c` | keramics `keramics-formats/tests/sparsebundle.rs`; libmodi 20260902 (`pymodi`) reads the same |
| `dmg-sparsebundle.sparsebundle` | 3145728 bytes, SHA-256 `770be732aafc4962c6940a2076c35471419621d9d6ca57cda60cbd6e82a8b36f` | ewfprobe `tests/fixtures/manifest.json` (`source.raw`, the image hdiutil was given); libmodi reads the same |

The medium carries a GUID partition map (hdiutil's default) whose one Apple_HFS partition holds
the entries keramics' `scripts/shared_macos.sh` (`create_file_entries`) made: `emptyfile`,
`testdir1/testfile1` (`Keramics` and a newline, hard-linked as `file_hardlink1`, so its data
lives in an indirect node file), `file_symboliclink1` -> `/Volumes/hfsplus_test/testdir1/testfile1`,
`directory_symboliclink1` -> `/Volumes/hfsplus_test/testdir1`, and others. The script's
`testdir1/TestFile2` is not in the catalog; the `Apache License` text in the medium is the large
extended attribute of `testdir1/large_xattr`.

## Licences

- keramics: Apache License 2.0, Copyright 2024-2026 Joachim Metz
  (https://www.apache.org/licenses/LICENSE-2.0). The repository ships no NOTICE file.
- ewfprobe: MIT License, Copyright (c) 2026 Alexis Brignoni. Permission is hereby granted, free of
  charge, to any person obtaining a copy of this software and associated documentation files (the
  "Software"), to deal in the Software without restriction, including without limitation the
  rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the
  Software, and to permit persons to whom the Software is furnished to do so, subject to the
  following conditions: The above copyright notice and this permission notice shall be included in
  all copies or substantial portions of the Software. THE SOFTWARE IS PROVIDED "AS IS", WITHOUT
  WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
  MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
  AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN
  ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR
  THE USE OR OTHER DEALINGS IN THE SOFTWARE.
