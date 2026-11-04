# ONEPKG reference vector

No OneNote-exported package with a redistributable licence could be found, and no OneNote is
available here to export one, so the fixture is the container a OneNote package is (PRONOM `fmt/987`:
an `MSCF` cabinet, format 1.3, of `.onetoc2`/`.one` members) built by a third-party writer:

| File | Origin | SHA-256 |
| --- | --- | --- |
| `makecab-package.onepkg` | Windows 11 `makecab.exe /F` with `CompressionType=MSZIP`, one folder, packing `testOneNote.one` and `test-tika-4303-Chinese-notes.one` from `../../OneNote/ReferenceVectors/` (Apache Tika test corpus, Apache-2.0) | `09aa6f0089361d539ba8c3684912c468fba4393c5c86007f9d393c316e79b5cd` |

7-Zip 26.04 lists it as an MSZIP cabinet of those two members (30288 and 51816 bytes). The tests
check that every extracted section is byte-identical (SHA-256) to its source file.
