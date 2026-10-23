# AFF (Advanced Forensic Format, AFFLIB v3) — on-disk notes

`FileFormat.Aff` is implemented from this page. AFFLIB
([sshock/AFFLIBv3](https://github.com/sshock/AFFLIBv3), revision
`f35df6c1d2610e3233c30d50054c00e29d7d5a23`) is under the four-clause BSD
licence, whose advertising clause is incompatible with LGPL. Its sources were
read only to write down what the format is. No code was taken from them.
The background is S. Garfinkel et al., *AFF: A New Format for Storing Hard Drive
Images* (2006). AFFLIB's own tools (`affconvert`, `affcat`, `affinfo`,
`affverify` from Debian/Ubuntu `afflib-tools`) are the black-box oracle in
`Compression.Tests/Aff/AffExternalConformanceTests.cs`.

All binary numbers are big-endian (network order).

## File

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 8 | `41 46 46 31 30 0D 0A 00` (`"AFF10\r\n\0"`) |
| 8 | … | segments, back to back, to end of file |

Source: `afflib_i.h` (`AF_HEADER`, `struct af_head`).

## Segment

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 4 | `"AFF\0"` |
| 4 | 4 | name length *n* |
| 8 | 4 | data length *d* |
| 12 | 4 | argument (32-bit, meaning depends on the name) |
| 16 | *n* | name (ASCII, not terminated) |
| 16+*n* | *d* | data |
| 16+*n*+*d* | 4 | `"ATT\0"` |
| 20+*n*+*d* | 4 | segment length = 24 + *n* + *d* |

A segment with an empty name is free space, left behind when AFFLIB deletes or
moves a segment. Readers skip it. When a name appears twice, the later
segment is the live one, since AFFLIB updates by appending. Source:
`afflib_i.h` (`struct af_segment_head`, `struct af_segment_tail`);
`vnode_aff.cpp` (`aff_write_seg`, `aff_get_next_seg`, `aff_write_ignore`).

## Segment names

| Name | Encoding | Meaning |
| --- | --- | --- |
| `pagesize` (legacy `segsize`) | argument | bytes per page; AFFLIB defaults to 16 MiB and rejects more |
| `sectorsize` | argument | bytes per sector (default 512) |
| `imagesize` | 8-byte quad, argument 2 | image length in bytes |
| `devicesectors` | 8-byte quad, argument 2 | image length in sectors |
| `badflag` | data, one sector | the pattern AFFLIB writes for unreadable sectors |
| `badsectors`, `blanksectors` | quad | counts |
| `page`*N* (legacy `seg`*N*) | data, flags in argument | page *N* of the image, covering bytes `N × pagesize` onward |
| `page`*N*`_md5` / `_sha1` / `_sha256` | data | hash of that page |
| `md5`, `sha1`, `sha256` | data (16/20/32 bytes) | hash of the whole image |
| `acquisition_*`, `case_num`, `image_gid`, … | data (text or binary) | acquisition metadata |
| `affkey_aes256`, `affkey_evp`*N*, names ending `/aes256` | — | encrypted image (session key, encrypted segments) |
| names ending `/sha256`, `cert-sha256` | — | segment signatures and the signing certificate |
| `dir` | — | an optional table of contents; readers can walk the segments instead |

**Quad**: 8 bytes. The low 32 bits come first, then the high 32 bits, each
big-endian. Source: `afflib_i.h` (`struct aff_quad`); `afflib_util.cpp`
(quad decode); `afflib.h` (`AF_SEG_QUADWORD` = 2).

If `imagesize` is missing, the size is `highest page number × pagesize` plus
the decoded length of that page. Source: `afflib_pages.cpp` (`af_read_sizes`).

## Page argument flags

| Bits | Value | Meaning |
| --- | --- | --- |
| 0x0001 | `AF_PAGE_COMPRESSED` | data is compressed; otherwise it is the page verbatim |
| 0x0002 | `AF_PAGE_COMP_MAX` | informational ("compressed at maximum") |
| 0x00F0 | algorithm mask | `0x00` zlib, `0x10` bzip2 (never implemented), `0x20` LZMA, `0x30` ZERO |

- **zlib**: a zlib stream with header and Adler-32 (AFFLIB calls zlib's
  `uncompress`). The decoded length is the page length.
- **LZMA**: the 13-byte "LZMA alone" header (5 property bytes, then the
  uncompressed size as 8 bytes little-endian), followed by the raw LZMA stream.
  Source: `lzma_glue.cpp` (`lzma_uncompress`).
- **ZERO**: exactly 4 data bytes, a big-endian count. The page is that many NUL
  bytes. AFFLIB writes ZERO for every all-zero page, with flags `0x0033`.
  Source: `afflib_pages.cpp` (`af_get_page`, `af_update_page`).

Every page except the last holds `pagesize` bytes. The last holds the remainder.

## Missing pages

A page that is absent was never written. AFFLIB reads it as zeros. Only with
`AF_BADBLOCK_FILL` does it read it as repeated `badflag` sectors. Source:
`afflib_pages.cpp` (`af_get_page`).

## Writer profile

What `FileFormat.Aff` writes, and what the oracle tests check `affinfo`,
`affcat` and `affverify` accept:

1. the header;
2. `pagesize` (argument), then `sectorsize` (argument);
3. any acquisition segments kept from the source image;
4. `imagesize` and `devicesectors` (quads, argument 2);
5. `page0` … `pageN`: ZERO for all-zero pages, zlib when that is smaller,
   otherwise stored;
6. `md5` and `sha1` of the whole image.

When the image is replaced, everything derived from it is regenerated: pages,
page hashes, sizes and image hashes. Every other segment is copied unchanged.
Signed or encrypted images are not rewritten, because their signatures and keys
cannot be regenerated.

## AFD and AFM

`.afd` is a directory of `.aff` files, each holding a run of pages. `.afm` is a
raw image plus an `.aff` file that carries only the metadata. Both need more
than one stream. The single-stream archive API refuses them; open each `.aff`
on its own.
