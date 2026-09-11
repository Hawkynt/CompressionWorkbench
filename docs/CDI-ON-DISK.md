# DiscJuggler CDI on-disk notes

DiscJuggler's CDI format has no public Padus specification. The implementation in
`FileFormat.Cdi` therefore uses a clean-room description distilled from observable
container behaviour and independent readers rather than copying an implementation.

## Sources and licensing

- **CDIrip** (`jozip/cdirip`), GPL-2.0: used as a behavioural oracle for the
  v2/v3/v3.5 trailer contract and the minimal session/track record fields. No
  implementation code, comments, naming or control flow was copied.
- **Aaru DiscJuggler reader**, LGPL-2.1-or-later: used as an independent cross-check
  for the trailing descriptor model, cooked/raw sector sizes and Mode-1/Mode-2
  interpretation. Its newer descriptor parser is not used as source code.
- Padus DiscJuggler itself is proprietary and no normative file-format document was
  found.

The values below are interoperability facts: byte order, version constants, field
sizes and marker bytes are dictated by existing CDI files/readers rather than by an
implementation's expressive choices.

## Trailer

The final eight bytes are little-endian:

| Offset from EOF | Size | Meaning |
| ---: | ---: | --- |
| -8 | 4 | Version: `0x80000004` (v2), `0x80000005` (v3), `0x80000006` (v3.5) |
| -4 | 4 | Descriptor locator |

For v2/v3 the locator is an absolute file offset. For v3.5 it is the descriptor
length measured backwards from EOF, so `descriptorStart = fileLength - locator`.
A zero locator is not a valid CDIrip CDI; CompressionWorkbench accepts it only as a
legacy compatibility profile because older versions of this repository emitted
exactly that synthetic trailer.

## Writer profile

The writer intentionally emits a narrow profile:

- CDI v3.5;
- one session;
- one Mode-1 data track;
- cooked 2,048-byte sectors;
- ISO 9660 begins at file offset zero;
- no pregap, subchannel or CD-Text payload;
- descriptor after the track data;
- trailer locator equals the complete descriptor length.

The descriptor contains the session count, track count, the two ten-byte track
start markers (`00 00 01 00 00 00 FF FF FF FF`), zero-length source filename,
pregap/length/mode/start-LBA/total-length, and sector-size selector required by the
v3.5 reader contract. Optional extension blocks are absent.

This profile is deliberately smaller than the full DiscJuggler format. Multi-session
Dreamcast layouts, audio tracks, subchannel data and the newer descriptor dialects
remain read/write follow-up work rather than being guessed at.

## Mutation and maintenance

File-level add/replace/remove is a verified extract -> edit -> re-create operation.
That is the only mutation namespace advertised for descriptor-bearing CDI files.
The old `sector-NNNNNN.bin` editor remains available for footer-only legacy images
and as the explicit `CdiInPlaceModifier` low-level API; it is not allowed to grow a
real descriptor-bearing image because doing that without updating track lengths
would leave a structurally inconsistent CDI.

`defrag`, `shrink` and therefore the `compact` composite use the repository's
verified rebuild mechanisms. `purge` follows from the same file-level modifier.
`wipe` is not advertised: safely identifying every byte that is free requires a
complete optical-track plus ISO allocation map. `layout` is also not advertised:
the supported writer profile has fixed 2,048-byte cooked sectors and no meaningful
alternative allocation geometry.
