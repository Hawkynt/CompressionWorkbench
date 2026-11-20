# StuffIt X archives with a recovery record — what is measured

StuffIt's "recoverability" (StuffIt 7) and "redundancy" (StuffIt Deluxe 2009/2010) options write
a StuffIt X archive that neither this repository nor The Unarchiver (unar 1.10, and XADMaster at
`7cb9ee0a…`) can read. This page records what was measured on the three such archives of the CC0
corpus (`ssokolow/stuffit-test-files`), each compared with the plain archive StuffIt wrote from the
same sources. Nothing here is decoded yet; the reader refuses these archives by their root element.

## Samples

| Archive | Size | Plain counterpart | Size |
| --- | ---: | --- | ---: |
| `testfile.stuffit7_dlx.mac9.recoverability.sitx` | 263,691 | `testfile.stuffit7_dlx.mac9.sitx` | 2,074 |
| `testfile.stuffit_deluxe_2009.win.redundancy.sitx` | 263,502 | `testfile.stuffit_deluxe_2009.win.sitx` | 1,239 |
| `testfile.stuffit_deluxe_2010.win.redundancy.sitx` | 263,499 | `testfile.stuffit_deluxe_2010.win.sitx` | 1,229 |

## Confirmed

- The file starts with `StuffIt!` and a root element (type 7) carrying algorithm key 5 = 0, which
  plain archives never set. The element after it does not parse as an element: the archive body
  is not an element stream from there on.
- After the root element: a little-endian `uint32` 0, a `uint32` 4, one byte that equals the chunk
  width below (10 for the Mac 9 archive, 5 for the Windows ones), then 16 bytes that differ per
  archive, then zeros.
- From offset 1047 (Mac 9) / 1050 (Windows) the file is a run of 255 blocks, 1,029 bytes apart:
  `B3 B1`, a big-endian 24-bit block number 1-255, four bytes, then 1,020 payload bytes. Every
  eighth block is `B3 B2` and 8 bytes longer; those 8 bytes start `00 0A DF 37` in every archive.
  The last block carries one more byte.
- The four bytes after the block number are the same for the same block number in every archive
  (`88 46 E9 84` for block 1, `11 4F B8 3E` for block 2, …): they depend on the block number only,
  not on the payload.
- Block k's payload is zero except for one run of "chunk width" bytes starting at payload offset
  4·k (wrapping at the end of the block). 255 × width is close to the size of the plain archive.

## Refuted

- The plain archive's bytes appear nowhere in the file: no 8-byte (or 6-, 5-byte) window of the
  plain archive after its root element occurs, so the data is not stored verbatim, neither
  contiguously nor read block-by-block, column-wise, or along the 4·k diagonal (widths 4-12).
- The four bytes after the block number are not the CRC-32 (either byte order) of the payload.
- The diagonal chunks are not the plain archive XORed with one key stream shared by the archives:
  XORing each archive's chunks with its plain counterpart (from offset 8, 10 or 12) gives
  sequences that differ from archive to archive.

## Open

The chunks look like parity rather than data (for the 2010 archive they are mostly zero), so the
data itself may be held in a form that has yet to be found; StuffIt's own documentation of the
option was not available. A reader would need the inverse of that coding, verified against the
plain counterparts above, whose fork contents and CRCs are known.
