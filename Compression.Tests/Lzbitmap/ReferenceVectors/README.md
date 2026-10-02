# LZBITMAP reference pairs

`NNN-original` / `NNN-compressed` are the test corpus of
[eafer/libzbitmap](https://github.com/eafer/libzbitmap) (`test/files/`, revision
`574abeae25b25c3319b9b6a9225cc7464e08ae88`, fetched 2026-10-02 through the GitHub contents API),
copied byte for byte. Pairs 017 and 018 are empty upstream and are left out.

## Where the compressed files come from

libzbitmap's author reverse-engineered LZBITMAP by black-box testing Apple's implementation; the
repository does not say what wrote the compressed files. They are not libzbitmap's own output:
compressing each original with libzbitmap's algorithm (our conversion, `Lzbitmap.Compress`)
reproduces only 001, 012, 013 and 015, and the others differ in length and bytes. The corpus also
uses a shape libzbitmap never writes — a repetition count that runs past the end of the chunk
(004, 014, 016) — which a decoder has to stop at the decompressed length. Both point to another
encoder, consistent with Apple's libcompression, but that is inferred, not documented.

dissect.util 3.24's decoder (Apache-2.0) reads 13 of the 17 pairs: it overruns on the
past-the-end repetition counts and asks for a nibble after a chunk's last group (019).
libzbitmap's decoder and ours read all 17.

| Pair | Original bytes | Compressed bytes | SHA-256 (compressed) |
| --- | --- | --- | --- |
| 001 | 43 | 59 | `4b479096cb6e7950b6b0e1623670a841c6a669ad55868d488bc31842e393b1c3` |
| 002 | 160 | 55 | `09e4dee9996fbee35a2f09d0746eeaf92f42b21e57cdca347ff6b7e95b9b0343` |
| 003 | 160 | 62 | `940a00f6f5e6ee7e89bc3a0c798b03539c0607da77c30248325eddd80140b3d7` |
| 004 | 159 | 64 | `26b097ae75147741b87d4ccd25c6f3ee60e6b1de1fde19c071a9df500178c1a0` |
| 005 | 176 | 129 | `1892df5d295074286d9b4b00b86702a8040f8728ac3f422e0c7a18bfab30dd24` |
| 006 | 286 | 295 | `fe52e09566eebe2eab56d09589198d962d26d24443d04512b913b78521cfae7e` |
| 007 | 12838 | 5473 | `438247df59d15fe8a415232ad7ec62ba4e7c42a664c86dc9919c27be221f4a20` |
| 008 | 160 | 92 | `9e22fdc53c5e3558a2e10222c455c7eaedbf2fd0d8b2216783dd2e57b49a055e` |
| 009 | 224 | 108 | `b2509a6f65ff5f6a7710c181ffc0b2cc16f313eb8504a8ac86b6140eaff55846` |
| 010 | 304 | 128 | `040ae83cac2074d2d94b8ec407138f9f59c28ae9317b821df9bcc9a41a7d0bb6` |
| 011 | 496 | 177 | `40bfc0743a1b93a9111a4534cecd5d1f49927728712b709df0480f5232ca028f` |
| 012 | 98304 | 528 | `77f2c6f0f8bdbd0717aa3b1f693ab3fc31ef2bb30bdc362ab62d00e4301f96eb` |
| 013 | 98304 | 98332 | `c70b7a7bcd45ff49c0539b594d91546d746deb4c58bea2b7f6897a9dc1ec575c` |
| 014 | 33656 | 32832 | `c705a8e2b0156fade91c0ddea58f1385c3b4c7910682a1c897158c0efa14da70` |
| 015 | 32798 | 224 | `31c83859e37821dfdd06096cd1614c8903ea16d057d4cb52b73a428a7380e891` |
| 016 | 32968 | 243 | `9ada4a4589790ca90e5447e9dbbd113b76ff2960804da3bd94db008f7255757e` |
| 019 | 286 | 281 | `84580ff403ccdc759afdd0ef51eb96f691209c7b4498c64cabbeb262aee35958` |

## Licence

libzbitmap: MIT License, Copyright (c) 2022 Corellium LLC. Permission is hereby granted, free of
charge, to any person obtaining a copy of this software and associated documentation files (the
"Software"), to deal in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software,
and to permit persons to whom the Software is furnished to do so, subject to the following
conditions: The above copyright notice and this permission notice shall be included in all copies
or substantial portions of the Software. THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY
KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
