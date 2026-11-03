# MAR reference vectors

| File | Origin |
| --- | --- |
| `test-xz.mar` | mardor's test data (`tests/test-xz.mar`, mozilla-releng/build-mar master, fetched 2026-10-19): a Thunderbird `thunderbird-comm-esr` 100.0 update package, xz-compressed members, signature block with no signatures. |
| `test-bz2.mar` | mardor's test data (`tests/test-bz2.mar`, same revision): the same package with bzip2-compressed members and one RSA signature. |
| `mardor-old.mar` | written by mardor (build-mar master, Python 3.13, Windows 11) with `mar -c` from `a.txt` (`hello mar\n`) and `dir/b.bin` (bytes 0..255 four times): the original layout without signature block. |
| `mardor-product.mar` | same inputs, `mar -H release -V 130.0 -c`: signed-era layout, zero signatures, product-information block. |
| `mardor-xz.mar` | same as `mardor-product.mar` plus `-J` (members xz-compressed). |
| `mardor-listing.txt` | mardor's `MarReader` view of every file above: signature count (-1 = no block), product information, and each member's size, mode and the SHA-256 of its stored bytes. |

The two `test-*.mar` files are © Mozilla, distributed under the Mozilla Public License 2.0
(<https://mozilla.org/MPL/2.0/>), as part of mardor; they are unmodified.

Our writer must reproduce `mardor-old.mar` and `mardor-product.mar` byte for byte from the same inputs
(mardor recorded Windows' `0666` mode for both members).
