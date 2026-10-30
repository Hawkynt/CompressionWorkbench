# BlakHole (.bh) on disk — what was measured

BlakHole is a closed Windows archiver with no published format. This page records only what
was measured on the one sample with provenance: `test.bh` from the Archive Team sample corpus
(<http://sembiance.com/fileFormatSamples/archive/blakHole/>, 152 768 bytes, five members). It
is behaviour evidence in the sense of AGENTS.md: the reader is written from this page, and
everything here was checked against the sample. Each member's CRC-32 confirms that its decoded
bytes are right.

## Layout

The file is a sequence of member records with no archive header and no trailer. Each record is a
header followed by `compressed size` bytes of data. All integers are little-endian.

| Offset | Size | Field | Observed | Status |
| --- | --- | --- | --- | --- |
| 0 | 2 | signature | `BH` | confirmed |
| 2 | 2 | ? | `05 07` in every record | unknown; required to match |
| 4 | 2 | header length | 37 + name length | confirmed: the data starts there |
| 6 | 4 | ? | `25 D8 0C 00` in every record | unknown; required to match |
| 10 | 1 | method | 8 | 8 = raw Deflate (RFC 1951), confirmed by decoding |
| 11 | 4 | DOS date/time | `0x41819300` and similar | consistent with 2012-12-01, the date of the sample set |
| 15 | 4 | compressed size | | confirmed: the next record starts after it |
| 19 | 4 | original size | | confirmed: equals the decoded length |
| 23 | 4 | CRC-32 (IEEE) of the original data | | confirmed on all five members |
| 27 | 1 | attributes | `0x20` | DOS archive bit (assumed) |
| 28 | 5 | ? | zero | unknown; required to be zero |
| 33 | 4 | name length | 8, 9 | confirmed |
| 37 | n | name | `test.doc` … | confirmed (ASCII; the encoding of other characters is unknown) |

## Not known

- Methods other than 8: none are present in the sample. A reader must refuse them.
- What the two constant fields mean: their values are required to match exactly, so a variant
  is refused rather than misread.
- Directories, encryption, solid mode and multi-volume archives: none appear in the sample.

## Implementation

`FileFormat.BlakHole` reads exactly this shape and verifies every member's CRC-32. It refuses
writing, because there is no BlakHole program to check what we would write.
