# DAR reference archives

Every file here was written by `dar` itself, never by CompressionWorkbench. They pin what the
reader has to accept; `docs/DAR-ON-DISK.md` is the layout they were measured against.

## dar 2.8.6 (Windows build `dar64-2.8.6-win64`)

The source tree was two files under `/tmp/src`: `hello.txt` (`hello dar\n`) and `noise.bin` (3000
pseudo-random bytes).

| Files | Command | What they pin |
| --- | --- | --- |
| `single.1.dar` | `dar -Q -c single -R /tmp/src` | header flag `T`, TLV list with the data name only, trailer `T` |
| `sliced.1.dar` … `sliced.3.dar` | `dar -Q -c sliced -S 1500 -s 1200 -R /tmp/src` | header flag `E` on every slice, TLVs 2 (first slice size 1500), 1 (slice size 1200), 3 (data name); trailers `N`, `N`, `T` |

`dar -t sliced` accepts the set.

## dar 2.7.13 (Ubuntu 24.04 package `dar` 2.7.13-5.1build4, libdar 6.7.1)

Built by `make-fixtures.sh` (run inside WSL / Linux). The source tree, rebuilt byte for byte by
`DarTests.Tree` in the test code:

| Path | Content |
| --- | --- |
| `hello.txt` | `hello dar\n`, mtime 2001-02-03 04:05:06.123456789 UTC |
| `hard.txt` | hard link to `hello.txt` |
| `link` | symlink to `hello.txt` |
| `fifo` | named pipe |
| `empty.bin` | 0 bytes |
| `text.txt` | `The quick brown fox jumps over the lazy dog. ` × 50 (2250 bytes) |
| `escape.bin` | 244 bytes holding dar's escape prefix `AD FD EA 77 21` and three copies of its sparse prefix `AE FD EA 77 21 58` |
| `sparse.bin` | `head`, 70000 zero bytes, `tail` (dar stores the zeros as a hole) |
| `noise.bin` | 600 bytes of xorshift32 output (seed 2463534242), mode 0600 |
| `nnn…n` (200 × `n`) | `long` |
| `dir/deeper/file.txt` | `deep\n`; `dir` has mode 0751 |

| File(s) | Command (`-R /tmp/cwbdar/src`) | What it pins |
| --- | --- | --- |
| `tree.1.dar` | `dar -c tree` | tape marks, no compression, sparse data, hard link, FSA records |
| `tree-notape.1.dar` | `dar -c tree-notape -at` | no escape layer; the layout the writer produces |
| `tree-gzip.1.dar` … `tree-lzo.1.dar` | `dar -c tree-<algo> -z<algo>` for gzip, bzip2, xz, zstd, lz4, lzo | streaming compression; lz4 and lzo in dar's block framing |
| `tree-zstd-block.1.dar`, `tree-gzip-block.1.dar` | `dar -c … -z<algo>:6:16k` | block compression (header flag 0x0800, block size 16384) |
| `multi.1.dar` … `multi.3.dar` | `dar -c multi -S 3k -s 2k` | a three-slice set |
| `empty.1.dar` | `dar -c empty -R /tmp/cwbdar/empty` | a catalogue holding only the root |
| `encrypted.1.dar` | `dar -c encrypted -K aes:cwb-fixture -R /tmp/cwbdar/empty` | an encrypted archive the reader must refuse |

`dar -t` accepts every one of them (the encrypted one with `-K aes:cwb-fixture`).
