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

## Version 0x110 images

`v110-better.daa`, `v110-best.daa` and `v110-password.daa` (password `secret42`)
were written by PowerISO 9.5 for Windows (`piso.exe` from the official
`poweriso_setup.exe`, SHA-256 `208c63f5…6944`, run under wine) with
`-method better` and `-method best`, from generated files. The script is
`make-110.sh`. `v110-iso.sha256` records the SHA-256 and size of the ISO that
`piso convert` turns each one into. `better` images hold LZMA (with the x86
filter) and deflate chunks.

`piso-split-110.sh` makes a split 0x110 set on the spot (2.6 MB in 1 MiB
volumes, too large to commit). It is read from its first volume in an
ExternalInterop test.

## gBurner GBI images

`gbi-normal.gbi`, `gbi-best.gbi`, `gbi-password.gbi` (password `Gbi-Secret_1`) and the
split set `gbi-split.part01.gbi` … `part03.gbi` (128 KiB volumes, gBurner's smallest) were
written by gBurner 5.9 Free edition (official installer, run under wine 9.0) with Tools >
Convert Image from ISOs that `xorrisofs -no-pad -J -r` built from generated files. gBurner
writes GBI only from that dialog, so `make-gbi.sh` drives it with xdotool on a virtual X display.
`gbi-iso.sha256` records the SHA-256 and size of the ISO each image was made from; the split set
holds a larger ISO with 200 000 more random bytes.
