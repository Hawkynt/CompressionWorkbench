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

## Password-protected and split images

`pw.daa`, `splitpw.part01.daa` and `splitpw.part02.daa` were written by the same
PowerISO 1.3, with the password `Secret-30`, from ISOs that `genisoimage -r -J`
built from generated files (Python `random.Random(30)`; the script is
`make-protected.sh`):

    poweriso convert a.iso -o pw.daa -setpassword Secret-30
    poweriso convert b.iso -o splitpw.daa -volsize 300K -setpassword Secret-30

`a.iso` (507 904 bytes, SHA-256 `b8f9ec03…28603`) holds 20 000 seeded words,
5 000 random bytes and a small file. `b.iso` (899 072 bytes, SHA-256
`49c55170…14ae`) holds 340 000 random bytes and 30 000 seeded words, so it
splits into two volumes (300 KiB is PowerISO's smallest volume size). PowerISO
converts both back to the identical ISOs. `DaaTests` pins both SHA-256 values.
