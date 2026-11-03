# EWF2-Ex01 vectors

Written by `ewfacquire -f encase7-v2` from **libewf-experimental-20240506** (Joachim Metz,
LGPL-3.0-or-later; the first release whose `ewfacquire` writes EWF2, built from its release
tarball in WSL, Ubuntu 24.04) on 2026-11-03 by `mkfixtures.sh` (documentation, not a build
input). Each image is stored gzipped; `sources.sha256` holds the SHA-256 of the medium it was
acquired from, which is what the reader must return.

| File | Made with | What it covers |
| --- | --- | --- |
| `deflate.Ex01` | `-c deflate:best` on a 192 KiB medium (text, zeros, random) | zlib chunks, chunks stored with an Adler-32 trailer (the incompressible ones), pattern-filled zero chunks, MD5 section |
| `stored.Ex01` | `-c deflate:none` on the same medium | stored chunks with Adler-32 trailers, pattern-filled zero chunks |
| `multi.Ex01` .. `multi.Ex03` | `-c deflate:none -S 1MiB` on a 2.5 MiB medium | a three-segment set ("next" sections, a sector table per segment) |

No EnCase-written Ex01 is available to this project, so libewf is the only producer. Its
pattern-filled chunks carry the 8 pattern bytes in the sector data with size 8, where the
specification has the pattern in the table entry's offset and size 0; the reader accepts both.
libewf's own reader then misreads those chunks (its `ewfverify` reports an MD5 mismatch on these
images and `ewfexport` returns other bytes for them), so the expected media come from the
acquired sources, not from `ewfexport`.
