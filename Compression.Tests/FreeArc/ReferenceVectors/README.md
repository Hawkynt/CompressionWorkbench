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
| `ppmd.arc` | `-mppmd` | hello, lines | `ppmd:10:48mb` — refused |
| `tornado.arc` | `-m1` | hello, lines | `rep:…+4x4:tor:…` — refused |
| `encrypted.arc` | `-mlzma -pSecret` | hello | `lzma…+aes-256/ctr:…` data — refused |
| `hdrenc.arc` | `-mlzma -hpSecret` | hello | encrypted directory — listed as `FULL.arc` + `metadata.ini` |
