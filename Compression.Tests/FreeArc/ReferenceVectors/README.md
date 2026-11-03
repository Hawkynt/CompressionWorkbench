# FreeArc reference archives

Every `.arc` file here was written by FreeArc 0.67 (2014-03-15) `Arc.exe`, taken from the `bin`
folder of `FreeArc-console-0.67-alpha-win32.exe` (Wayback Machine copy of
`freearc.org/download/testing/`), on Windows 11. FreeArc is GPL/freeware and is used only as a black
box; see `docs/FREEARC-ON-DISK.md`.

Inputs (all files and directories touched to 2024-01-02 03:04:05 UTC; FreeArc, run on Windows during
daylight saving time, recorded them as 04:04:05 UTC, and that is what the archives hold):

| Name | Content |
|------|---------|
| `hello.txt` | `Hello, FreeArc!\n` (16 bytes) |
| `empty.bin` | 0 bytes |
| `sub/lines.txt` | `line N the quick brown fox jumps over the lazy dog\n` for N = 1..40 (2071 bytes) |
| `dир/файл.txt` | `abc` |
| `emptydir/` | empty directory |

Each archive was made with `Arc.exe a <archive> -i0 -cfg- <switches> <files>`:

| File | Switches | Files | What it covers |
|------|----------|-------|----------------|
| `storing.arc` | `-m0 -dmstoring` | hello, empty, lines | everything uncompressed; our writer reproduces it byte for byte |
| `lzma.arc` | `-mlzma` | hello, empty, lines | `lzma:32kb:normal:32` data, lzma-compressed directory and footer |
| `lzma-lc4.arc` | `-mlzma:lc4:lp1:pb1` | hello, empty, lines | non-default literal/position bits |
| `dirs.arc` | `-mlzma -s- -r` | emptydir, dир (recursive), hello | directory entries, UTF-8 names, three solid blocks |
| `comment.arc` | `lzma.arc`, then `Arc.exe c comment.arc "--archive-comment=A FreeArc comment"` | | archive comment in the footer |
| `ppmd.arc` | `-mppmd` | hello, lines | `ppmd:10:48mb` |
| `tornado.arc` | `-m1` | hello, lines | `rep:…+4x4:tor:…` |
| `encrypted.arc` | `-mlzma -pSecret` | hello | `lzma…+aes-256/ctr:…` data — refused |
| `hdrenc.arc` | `-mlzma -hpSecret` | hello | encrypted directory — listed as `FULL.arc` + `metadata.ini` |

`mkfilters.py` makes twenty-nine more from generated inputs (text, a 24000-byte random run repeated 3000
bytes later, a table of 8-byte records, x86-like code, an empty file; the `text-*` archives hold one
file of sentences on lines instead), each with
`Arc.exe a <archive> -i0 -cfg- -ep1 <switch>`; `filters.tsv` lists what `Arc.exe x` extracts:

| File | Switch | Method FreeArc recorded |
|------|--------|-------------------------|
| `filters-rep.arc` | `-mrep:1mb` | `rep:153kb` |
| `filters-delta.arc` | `-mdelta` | `delta` |
| `filters-exe.arc` | `-mexe` | `exe` |
| `filters-mx.arc` | `-mx` | `rep:153kb+exe+delta+lzma:153kb:normal:bt4:128` |
| `filters-4x4.arc` | `-m4x4:b64kb:lzma` | `4x4:b64kb:lzma:153kb:normal:32` (three chunks) |
| `filters-m5x.arc` | `-m5x` | `exe+delta+4x4:lzma:153kb:normal:32:mc32` |
| `tor-1.arc`, `tor-2.arc`, `tor-3.arc` | `-mtor:1`, `-mtor:2`, `-mtor:3` | Tornado byte, bit and Huffman coders |
| `tor-5.arc`, `tor-12.arc` | `-mtor:5`, `-mtor:12` | arithmetic coder, lazy and optimal parsing |
| `filters-m1.arc`, `filters-m2.arc` | `-m1`, `-m2` | `rep+4x4:tor:3`; `-m2` also `rep+exe+4x4:tor` in a second block |
| `ppmd-6.arc` | `-mppmd:6:16mb` | PPMd var.I |
| `ppmd-restart.arc`, `ppmd-cutoff.arc`, `ppmd-freeze.arc` | `-mppmd:12:256kb`, `:r`, `:r2` | a model too small for the data, so every restore method runs |
| `grzip-m1.arc` .. `grzip-m4.arc` | `-mgrzip:m1` .. `:m4` | BWT or ST4 with WFC or MTF ranks, LZP on |
| `grzip-strong.arc`, `grzip-nolzp.arc` | `-mgrzip:m1:s`, `-mgrzip:m3:l` | strong-sorted BWT; ST4 without LZP |
| `grzip-delta.arc` | `-mgrzip:m2:d:b64kb` | three blocks; the last is record-filtered into four nested blocks, two of them stored after LZP |
| `text-m3.arc`, `text-m4.arc` | `-m3`, `-m4` | the text chains `grzip:…:m2:l32:h15` and `grzip:…:m1:l32:h15` |
| `lzp.arc` | `-mlzp` | LZP with the default parameters (minimum match 64, 2^18 table) |
| `lzp-short.arc` | `-mlzp:b64kb:12:h12:d8kb:s20` | short matches, a 2^12 table, an 8 KiB barrier and three blocks |
| `lzp-tor.arc` | `-mlzp:16:h16+tor:3` | LZP in front of tor, as FreeArc chains it |
