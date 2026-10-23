# zpaq 7.15 reference archives

Every `.zpaq` file here was written by the reference archiver **zpaq v7.15** (Matt Mahoney,
public domain), the Windows x64 build `zpaq64.exe` shipped in
<http://mattmahoney.net/dc/zpaq715.zip> ("compiled Aug 17 2016"), on 2026-10-19.

`mkfixtures.py` (documentation, not a build input) generated the input files deterministically
and ran `zpaq64.exe a <archive> <dir> [-method M]` in the work directory, so the stored names are
relative (`set/text.txt`). Only the archives and `manifest.tsv` are checked in: the manifest gives,
per archive and version, every name with its size and SHA-256 (size `-1` marks a directory).

| File | Layout | How it was made | What it exercises |
|------|--------|-----------------|-------------------|
| `set.m0.zpaq` .. `set.m5.zpaq` | journaling | `a set.mN.zpaq set -method N` | every predefined method: 0 (dedup only), 1/2 (LZ77, packed codes, hash and suffix-array match finders), 3 (BWT or LZ77+CM), 4/5 (CM with ICM/ISSE chains, match model, mixers, SSE); E8E9 on `x86.bin`; `dup.txt` deduplicated against `text.txt`; an empty file; a subdirectory |
| `versions.zpaq` | journaling | three `a versions.zpaq v` runs: add `notes.txt`+`a.bin`; change `a.bin`, add `b.txt`; delete `notes.txt` | three transactions, an update, a deletion record, fragments shared across versions |
| `stream.zpaq` | streaming | `a stream.zpaq st -method s4.3ci1` | streaming layout (one block per file, filename and date in the segment header), BWT + order-0 ICM + order-1 ISSE |

`zpaq64.exe l versions.zpaq -all` reports `3 versions, 6 files, 4 fragments`; `zpaq64.exe l
stream.zpaq` reports two files of unknown size (the streaming layout records none).
