# DiscJuggler CDI on-disk notes

DiscJuggler's CDI format has no public Padus specification. `FileFormat.Cdi` uses a
clean-room description of the observable container contract rather than translating
a third-party reader.

## Sources and licensing

- **No$cash / psx-spx CD-ROM documentation**: independent format notes for the
  session block, Track/Disc Header, variable index and CD-Text fields, track block,
  read modes and Disc Info block. These field facts are treated as interoperability
  data.
- **CDIrip** (`jozip/cdirip`), GPL-2.0: behavioural oracle for v2/v3/v3.5 trailer
  semantics and an older descriptor dialect. No implementation code, comments,
  naming, structure or control flow was copied.
- **Aaru DiscJuggler reader**, LGPL-2.1-or-later: independent cross-check for
  multisession track walking, file-offset accumulation, pregaps, audio/Mode-1/Mode-2
  interpretation and read modes 0-4. Its implementation is not translated.
- **mkdcdisc** (`Mark65537/mkdcdisc`), MIT: Dreamcast-oriented CDI writer used as a
  compatible writer oracle. It independently confirms the physical overlap between
  the seven-byte session preamble and the first eight bytes of the following track
  header, plus the v3.5 Disc Info/footer framing.
- Padus DiscJuggler itself is proprietary; no normative Padus file-format document
  was found.

The constants and field layouts below are interoperability facts dictated by CDI
media and independent implementations.

## Body and descriptor

A CDI is not one flat sector array with one global geometry. Before the trailing
descriptor it stores each track consecutively, including its index-0 pregap. The
stored bytes per sector are selected per track:

| Read mode | Stored bytes/sector | Meaning |
| ---: | ---: | --- |
| 0 | 2048 | cooked Mode 1 |
| 1 | 2336 | cooked Mode 2 |
| 2 | 2352 | raw sector / CD-DA audio |
| 3 | 2368 | raw 2352 + 16-byte P/Q subchannel |
| 4 | 2448 | raw 2352 + 96-byte P-W subchannel |

For filesystem access, Mode-1 user data begins at byte 0 in read mode 0 or byte 16
in raw modes. Mode-2 Form-1 user data begins at byte 8 in read mode 1 or byte 24 in
raw modes. Audio remains raw and is exposed through `CdiReader.ReadTrackSector`;
it is not presented as an ISO file.

The descriptor begins with a session count. Each logical 15-byte session block
contains its track count and ends in the first eight bytes of the following physical
track header. Consequently the logical Track/Disc Header starts eight bytes into
that physical structure. Its stable 12-byte marker is:

```text
FF FF 00 00 01 00 00 00 FF FF FF FF
```

Each track then carries a variable filename, an index-length array, optional CD-Text,
mode, session/track numbers, start LBA, total length, read mode and control flags.
The index lengths matter: index 0 is the stored pregap, later indices are the live
track data. The reader derives physical file offsets by accumulating each track's
stored sector count times its own stride; it never multiplies a disc LBA by one
global sector size.

## Trailer

The final eight bytes are little-endian:

| Offset from EOF | Size | Meaning |
| ---: | ---: | --- |
| -8 | 4 | Version: `0x80000004` (v2), `0x80000005` (v3), `0x80000006` (v3.5) |
| -4 | 4 | Descriptor locator |

For v2/v3 the locator is an absolute file offset. For v3.5 it is the descriptor
length measured backwards from EOF, so `descriptorStart = fileLength - locator`.
A zero locator is not a valid normal CDIrip image; CompressionWorkbench accepts it
only because older versions of this repository emitted that synthetic trailer.

## Reader profile

The reader parses real session/track tables and supports:

- multiple sessions and multiple tracks per session;
- stored pregaps;
- CD-DA/audio tracks through raw sector access;
- Mode 1 and Mode 2 data tracks;
- read modes 0 through 4, including raw sectors with appended subchannel data;
- ISO 9660 from the latest data track containing a valid PVD at track-relative
  sector 16;
- Dreamcast-style second-session images whose ISO directory extents are disc-absolute
  LBAs rather than track-relative LBAs.

That last distinction is important for classic self-boot Dreamcast images. Tools
historically create the second ISO session with an explicit session start (for
example around LBA 11700), so directory records may point at 11718/11719 rather
than 18/19. Once the root extent proves that convention, the reader resolves each
ISO extent through the parsed CDI track map. This also permits absolute extents to
refer to another data track when a multisession ISO legitimately does so.

Mode-2 Form-1 is decoded for ISO use. Form-2 payload semantics are not currently
promoted to filesystem entries; callers can still retrieve the complete stored raw
sector through `ReadTrackSector`.

## Writer profile

Creation remains deliberately narrower than reading:

- CDI v3.5;
- one session;
- one Mode-1 data track;
- cooked 2,048-byte sectors;
- a standard 150-sector index-0 pregap stored before ISO sector 0;
- index 1 contains the ISO sectors;
- no subchannel or CD-Text payload;
- a normal terminal zero-track session / Disc Info structure;
- final locator equals the complete descriptor length.

The writer therefore emits a normal descriptor-bearing image while avoiding claims
that it can author arbitrary mixed-mode optical layouts.

## Mutation and maintenance

File-level add/replace/remove, purge, defrag and shrink rebuild the image only when
the source layout is exactly the creator's single-session, single cooked Mode-1
profile. A mixed/multisession/audio/Mode-2/raw-sector image is readable but those
operations fail before writing anything: flattening it to one ISO track would lose
pregaps, audio, subchannels and session addressing.

The old `sector-NNNNNN.bin` editor remains only for the historical footer-only
CompressionWorkbench profile. `CdiInPlaceModifier` now refuses real descriptor-
bearing images entirely; one flat `LBA * sectorSize + offset` formula is invalid
for a CDI whose tracks have different strides.

`wipe` stays absent. Safely zeroing unused bytes requires a complete allocation map
that combines every optical track/index with the filesystem allocation state, not
just a guess based on ISO extents.

`layout` also stays absent. The current creator exposes no meaningful alternate
optical geometry to choose from; implementing the marker merely to render a green
cell would not provide a real operation.
