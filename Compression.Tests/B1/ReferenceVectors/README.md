# B1 reference vectors

The oracle is the b1-pack CLI (`org.b1.pack.cli.Main`, Apache-2.0). It was compiled with javac
(JDK 21) from <https://github.com/b1-pack/b1-pack> at eb2c91854082c37d1f3fcc4edcc7bdfd5806dfc1,
against the jars its POM names: lzma-sdk-4j 9.22.0, guava 11.0.2, jopt-simple 4.3,
sc-light-jdk15on 1.47.0.2 and jsr305 1.3.9.

## Written by b1-pack

The input tree:

| Item | Content | SHA-256 |
| --- | --- | --- |
| `docs/notes.txt` | 600 generated lines (19 036 bytes) | `db617d87…a790` |
| `docs/sub/tiny.txt` | `tiny` | `8950abfd…e10c` |
| `noise.bin` | 8 000 LCG bytes | `3777f85a…412d` |
| `empty.txt` | empty | |

The files were stamped 2024-05-06 05:08:10 UTC (`touch -t` in CEST).

| File | Command | Exercises |
| --- | --- | --- |
| `b1-default.b1` | `b1 a` | the default "smart" method stores everything in plain blocks |
| `b1-classic.b1`, `b1-maximum.b1` | `b1 a -m classic` / `-m maximum` | one LZMA chunk with end marker |
| `b1-aes.b1` | `b1 a --encrypt AES --password secret` | AES-256-GCM blocks, encrypted head and tail |
| `b1-multi.part01.b1` | first of four `-v 8kB` volumes | refused: only single-volume archives are read |

## Ours, read by b1-pack

`ours-lzma.b1`, `ours-store.b1` and `ours-aes.b1` (password `pw`) are `B1Tests.WriteOurs`.
`ours-edited.b1` is `b1-classic.b1` after our `Add("added.txt")` and `Remove("docs/sub")`.
`b1 l` lists each one, and `b1 x` extracts every file with the expected SHA-256
(`b1pack-listing.txt`).
