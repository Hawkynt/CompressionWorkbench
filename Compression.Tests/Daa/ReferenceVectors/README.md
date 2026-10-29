# DAA reference vector

`small.daa` (2 237 bytes, SHA-256 `12611771ad4e57ff36677395421f13834f2378ec51761279a3bf15b6292be218`)
was written by PowerISO for Linux 1.3 (`poweriso-1.3.tar.gz`, SHA-256
`605ae3fc…65fc0`) with:

    genisoimage -quiet -V DAAFIXTURE -o small.iso -J -R src
    poweriso convert small.iso -o small.daa -ot daa

Here `src` holds `readme.txt` ("PowerISO DAA reference vector\n") and
`sub/fox.txt`, which is 150 000 bytes of repeated
"the quick brown fox jumps over the lazy dog\n".
`poweriso convert small.daa -ot iso` reproduces `small.iso` byte for byte. Its
SHA-256 (`806dd112…1497`) is pinned in `DaaTests.SmallIsoSha256`.
