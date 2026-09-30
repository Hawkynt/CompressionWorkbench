# MOBI reference book

`pg1065.mobi` is Project Gutenberg eBook #1065, *The Raven* by Edgar Allan Poe, as served
unmodified by `https://www.gutenberg.org/ebooks/1065.kindle.noimages` (fetched 2026-09-30). Its
EXTH records name calibre 4.17.0 as the producer. The text is in the public domain in the USA, and
the file carries the Project Gutenberg License it is redistributed under.

It is a MOBI 6 book with PalmDOC compression, UTF-8 text and extra record data flags `0x0003`
(multibyte-overlap and indexing trailing entries on every text record).

Expected output for the tests came from KindleUnpack 0.4.1 (python package `mobi`):
`MobiHeader.getRawML()` returns 33778 bytes with SHA-1 `4d1697b820c78335a429b97382ce9881b9c49a28`,
the title is `The Raven` and the creator is `Edgar Allan Poe`.
