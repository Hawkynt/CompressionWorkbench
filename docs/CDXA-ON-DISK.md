# CD-ROM XA in a RIFF CDXA file

A file recorded in CD-ROM XA Mode 2 can be copied off a disc. Video CD MPEG tracks are the common
case: `MPEGAV/AVSEQnn.DAT`. Such a copy arrives as a RIFF file that carries the raw sectors. This
page describes that wrapper, the sector layout, and the Video CD sector conventions that our writer
reproduces. Every rule below was measured on a real Video CD track and on discs authored by GNU
VCDImager 2.0.1, and is pinned by `CdXaExternalConformanceTests`.

## RIFF wrapper

| Offset | Size | Content |
| --- | --- | --- |
| 0 | 4 | `RIFF` |
| 4 | 4 | file size − 8, little-endian |
| 8 | 4 | `CDXA` |
| 12 | 4 | `fmt ` |
| 16 | 4 | 16 |
| 20 | 16 | the file's XA attributes (below) |
| 36 | 4 | `data` |
| 40 | 4 | sector count × 2352 |
| 44 | … | raw 2352-byte sectors |

The `fmt ` body is the XA system-use field of the file's directory record, padded to 16 bytes:

- owner group ID (2 bytes, 0);
- owner user ID (2 bytes, 0);
- attributes (2 bytes; `15 55` on Video CD tracks);
- `XA`;
- the file number;
- seven zero bytes.

cdxa2mpeg expects `fmt ` and then `data`. Our reader accepts the chunks in any order and skips
unknown ones.

## Sector (ECMA-130, Mode 2 Form 2)

| Offset | Size | Content |
| --- | --- | --- |
| 0 | 12 | sync: `00`, ten × `FF`, `00` |
| 12 | 3 | address: minute, second, frame, in BCD (absolute; LSN + 150 frames) |
| 15 | 1 | mode, 2 |
| 16 | 8 | subheader: file number, channel, submode, coding information, then the same four bytes again |
| 24 | 2324 | user data |
| 2348 | 4 | EDC: CRC-32 (reflected polynomial `0xD8018001`, initial value 0, no final XOR) over offsets 16–2347, little-endian |

Submode bits: `0x01` end of record, `0x02` video, `0x04` audio, `0x08` data, `0x10` trigger,
`0x20` Form 2, `0x40` real time, `0x80` end of file.

## Video CD track layout

- The track opens with **30 empty sectors**. Their user data is all zero, and their subheader is
  the file number, channel 0, submode `0x60` and coding 0.
- Each **MPEG pack** then fills one sector, zero-padded to 2324 bytes. Data sectors use channel 1:
  - video packs: submode `0x62`, coding `0x0F`;
  - audio packs: submode `0x64`, coding `0x7F`. On the measured discs, audio packs are 2304 bytes,
    so their sectors end in 20 zero bytes.

  A pack's kind is the kind of its first PES stream: `0xE0`–`0xEF` is video and `0xC0`–`0xDF` is
  audio. A pack that holds only a system header and padding takes the kind of the first stream its
  system header lists. A track therefore opens with one such pack for video and one for audio.
- The **end-of-record** bit marks one of two places. VCDImager sets it on the last data sector
  only. The real track sets it on the last video sector and on the last audio sector. Our reader
  detects which convention a file follows (`record_ends` in `metadata.ini`), and our writer
  reproduces either one.
- The track closes with **45 empty sectors**. The last of them has submode `0xE0`, which adds end
  of file.
- The addresses run on without a gap from the first sector. VCDImager places `AVSEQ01.DAT` at
  LSN 450, address 00:08:00, which is our default.

`track.mpg` holds the user data from the first to the last non-empty sector, which is what
cdxa2mpeg extracts. Our writer splits it back into packs, ignoring the zero padding, and rebuilds
every sector from it and the layout above.

## Measured

- `AVSEQ09.DAT`, a real Video CD track of 11,663 sectors (provenance in
  `Compression.Tests/CdXa/ReferenceVectors/README.md`):
  - every EDC matches the formula above;
  - `track.mpg` equals cdxa2mpeg's output;
  - rebuilding from `track.mpg` and `metadata.ini` gives back the identical file.
- For an MPEG-1 clip encoded by ffmpeg (`-target pal-vcd`) and authored with `vcdimager -t vcd2`:
  - our file, created from the same clip, has exactly the sectors of `AVSEQ01.DAT` on the disc
    image;
  - cdxa2mpeg extracts our track from it.
