# ALZip reference vectors

| File | Origin |
| --- | --- |
| `bzip2.alz`, `bzip2_dlz.alz`, `deflate_pure.alz`, `deflate_empty.alz`, `dir_tree.alz`, `store_old.alz`, `store_empty.alz`, `enc_deflate.alz`, `enc_store.alz`, `zip20.alz` | unalz's test archives (`tests/data/`, alkegi/unalz master, fetched 2026-10-23), written by ALZip and dedicated to the public domain under CC0 1.0 by their author. Encrypted ones use the password `test1234`. |
| `ours-deflate.alz`, `ours-store.alz`, `ours-encrypted.alz` | written by our `AlZipWriter` from `AlZipReferenceTests.OursArchive` (fixed timestamps; the encrypted one with password `test1234`, its 12-byte headers random). |
| `alzip-split-sets.tar.gz` | split sets written by ALZip 12.38 (the official installer `ALZip1238.exe` from aldn.altools.co.kr, SHA-256 `3c1a44ad5e0a3c00304f34fbf56b8fa126dd5c84855164abaa590b81dab5f6fc`, installed under wine in a throwaway prefix), with `ALZipCon.exe -a -m<N> -v64KB "text.txt;sub;small.txt" <set>`. The inputs come from `make-split-inputs.py` and ship in the same tarball. ALZ at methods 0-2 (`.alz`, `.a00`, …) and EGG at methods 0-4 (`.vol1.egg`, …) give 2-4 volumes each. ALZip 12.38 writes nothing for an ALZ at method 3 (`-m3`), so no obfuscated-deflate sample can be made with it. |
| `unalz-listing.txt` | unalz 0.2.1 (built from that revision with cargo, Windows) extracting every archive above with `-P test1234`: path, size and SHA-256 of each file. |

ALZip stores a CRC-32 for every member, so the reader's own CRC check also ties each decoded member to
what ALZip wrote.
