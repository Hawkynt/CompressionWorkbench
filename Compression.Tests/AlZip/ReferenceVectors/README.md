# ALZip reference vectors

| File | Origin |
| --- | --- |
| `bzip2.alz`, `bzip2_dlz.alz`, `deflate_pure.alz`, `deflate_empty.alz`, `dir_tree.alz`, `store_old.alz`, `store_empty.alz`, `enc_deflate.alz`, `enc_store.alz`, `zip20.alz` | unalz's test archives (`tests/data/`, alkegi/unalz master, fetched 2026-10-23), written by ALZip and dedicated to the public domain under CC0 1.0 by their author. Encrypted ones use the password `test1234`. |
| `ours-deflate.alz`, `ours-store.alz`, `ours-encrypted.alz` | written by our `AlZipWriter` from `AlZipReferenceTests.OursArchive` (fixed timestamps; the encrypted one with password `test1234`, its 12-byte headers random). |
| `unalz-listing.txt` | unalz 0.2.1 (built from that revision with cargo, Windows) extracting every archive above with `-P test1234`: path, size and SHA-256 of each file. |

ALZip stores a CRC-32 for every member, so the reader's own CRC check also ties each decoded member to
what ALZip wrote.
