# DiscJuggler CDI on-disk notes

DiscJuggler's CDI format has no public Padus specification. `FileFormat.Cdi`
therefore implements a clean-room description of observable container behaviour
and verifies it against several independent readers/writers rather than treating
one reverse-engineered implementation as a specification.

## Sources and licensing

- **ECMA-119** defines the ISO 9660 filesystem carried by data tracks.
- **ECMA-130** defines CD-ROM Mode-1 framing, EDC, and the Annex-A P/Q RSPC
  error-correction matrices.
- **ECMA-168** defines the CD-ROM XA Mode-2 Form-1 field layout used for the
  2,048-byte filesystem payload.
- **CDIrip** (`jozip/cdirip`), GPL-2.0, is used only as a behavioural oracle for
  the older v2/v3 descriptor walk and the v2/v3/v3.5 trailer contract.
- **Aaru**, GPL-3.0, and **edccchk**, GPL-3.0, are behavioural oracles for CD
  sector interpretation/checking; neither is a source for implementation code.
- **libMirage CDI parser**, GPL-2.0-or-later, is used only as an independent
  behavioural oracle for the newer variable-index descriptor dialect.
- **mkdcdisc** is MIT overall and is useful for Dreamcast-oriented CDI structure,
  but its CD EDC/ECC source carries a separate CDDL-1.0-only notice. That encoder
  is therefore not copied or translated into this repository.

No GPL/CDDL implementation code, comments, naming, structure or control flow is
copied. Numeric markers, version IDs, field sizes, standard polynomials and
sector-mode constants are interoperability facts. The EDC and ECC lookup data used
by the mutation path is generated locally from the ECMA-defined polynomials rather
than copied as tables.

## Trailer

The final eight bytes are little-endian:

| Offset from EOF | Size | Meaning |
| ---: | ---: | --- |
| -8 | 4 | Version: `0x80000004` (v2), `0x80000005` (v3), `0x80000006` (v3.5) |
| -4 | 4 | Descriptor locator |

For v2/v3 the locator is an **absolute file offset**. For v3.5 it is the
**descriptor length measured backwards from EOF**, so:

```text
v2/v3: descriptorStart = locator
v3.5 : descriptorStart = fileLength - locator
```

A zero locator is not a normal DiscJuggler descriptor. CompressionWorkbench
accepts it only as a compatibility profile because older versions of this
repository emitted an ISO followed by `version + zero`.

## Two descriptor dialects

Real-world CDI files are not described by one stable record layout.
CompressionWorkbench recognizes both of the families needed by the independent
oracles.

### Older v2/v3 dialect

The legacy reader contract begins with a 16-bit session count. Each session has
a 16-bit track count, followed by track records containing:

- two ten-byte track markers: `00 00 01 00 00 00 FF FF FF FF`;
- optional source-filename data;
- pregap sector count;
- data-track length;
- track mode (`0` audio, `1` Mode 1, `2` Mode 2);
- start LBA and total stored length;
- stored-sector selector (`0` = 2048, `1` = 2336, `2` = 2352);
- v3-only extension space.

The physical image body is still a concatenation of all tracks. Pregap sectors
are physically stored, and the next track starts after `totalLength × sectorSize`.
The writer's v2/v3 compatibility targets intentionally emit the smallest canonical
form of this dialect: no embedded filename, no DJ4 extension and no optional v3
78-byte extension.

### Modern variable-index dialect

The modern descriptor begins with a byte-sized session count. Session blocks are
15 bytes; each track then has a variable filename, an index-length array, optional
CD-Text, mode/read-mode/control fields and a fixed trailing area. A final zero-track
session and a disc-info block terminate the descriptor.

The index list is significant: index 0 is the physically stored track pregap; the
following index lengths make up the track's data region. Read mode determines the
physical stride:

| Read mode | Stored sector bytes | Typical use |
| ---: | ---: | --- |
| 0 | 2048 | cooked Mode 1 |
| 1 | 2336 | cooked Mode 2 |
| 2 | 2352 | raw data / CD-DA |
| 3 | 2368 | raw 2352 + Q16 |
| 4 | 2448 | raw 2352 + P-W 96 |

## Writer compatibility targets

`CdiFormatDescriptor` exposes `TargetCompatibility` as a non-optimizer format
constraint:

- `3.5` (default): modern v3.5 single-session, single cooked Mode-1 track;
- `3.0`: old v3 descriptor dialect with absolute descriptor offset;
- `2.0`: old v2 descriptor dialect with absolute descriptor offset.

Fresh images use a standard 150-sector pregap and a cooked 2048-byte Mode-1 ISO
track. Selecting an old target changes the CDI descriptor/trailer contract, not
the ISO payload semantics. Raw-sector generation is currently used only to preserve
an existing track during mutation; it is not exposed as a separate fresh-image
creation profile.

## Multisession and Dreamcast addressing

A CDI body is **not one global sector geometry**. Every track has its own stride,
mode and pregap. File offsets are therefore calculated from the parsed track map,
not from `discLba × oneSectorSize`.

ISO 9660 multisession media uses absolute disc addressing for directory/path-table
extents even though the session's volume descriptor is physically located at track
LBA 16. Dreamcast-style second sessions rely on this. The reader detects whether
the selected data track uses relative or disc-absolute extents and maps either form
through the track table.

When CompressionWorkbench rebuilds such an embedded ISO, it emits the new directory
and path-table tree with the original track's absolute LBA bias. This includes both
primary and Joliet directory/path-table records and the recorded volume bounds.

## Clean-room CD sector integrity encoder

`CdiCdSectorIntegrity` is a separate managed implementation used only when an
existing raw data track must be rewritten.

For Mode 1 it regenerates:

- the 32-bit EDC over sync + address/mode + 2,048 user bytes;
- the eight reserved zero bytes;
- 172 P-parity bytes and 104 Q-parity bytes.

For Mode-2 Form 1 it preserves the existing XA subheader, regenerates the EDC over
subheader + 2,048 user bytes, and computes the same P/Q RSPC with the four address/
mode bytes treated as zero during parity generation as required by the XA layout.
The two four-byte XA subheader copies must agree; a mismatch is treated as malformed
rather than guessed into a writable profile.

The EDC table is generated from the reflected form of the ECMA-130 CRC polynomial.
The ECC implementation derives GF(2^8) multiplication from the Annex-A primitive
polynomial `x^8 + x^4 + x^3 + x^2 + 1` and walks the published 43-column P and
26-diagonal Q matrices directly. No external encoder tables are embedded.

Tests include deterministic full-sector known-answer hashes for Mode 1 and Mode-2
Form 1. The independently derived vectors were also checked against GPL-licensed
sector-checking implementations as behavioural oracles; no oracle source is copied
into the tests or production implementation.

## Mutation strategy

Supported descriptor-bearing ISO data tracks are edited transactionally without
regenerating the optical descriptor:

1. parse and save the complete session/track map;
2. verify that the selected data track is Mode 1 or Mode-2 Form 1 in a supported
   cooked/raw storage mode;
3. extract the active ISO filesystem;
4. apply add/replace/remove/purge or a defrag rebuild in a temporary tree;
5. build a fresh ISO using the source label/identifier/Joliet settings;
6. rebase ISO LBAs when the source track uses multisession absolute addressing;
7. require the rebuilt ISO to fit inside the existing data-track capacity;
8. stage a complete copy of the CDI and replace only each index-1 sector's
   2,048-byte user-data field; cooked Mode-1 sectors are written directly, while
   raw Mode-1 / Mode-2 Form-1 sectors have EDC/ECC regenerated;
9. preserve raw sync/address bytes, XA subheaders and appended Q16/P-W subchannel
   bytes exactly;
10. read the staged CDI back, require the exact same track map and verify every
    rebuilt file byte-for-byte;
11. only then replace the caller's stream, restoring the caller's original stream
    position on both successful and exceptional exits.

Consequently, audio tracks, all pregaps, other sessions, subchannel bytes,
undeciphered descriptor fields and the original v2/v3/v3.5 trailer dialect remain
byte-identical. This is particularly important for old v2/v3 images: mutation does
not pretend that every unknown historical descriptor field can be reconstructed.

### What remains read-only

Mode-2 Form-2, formless Mode-2 and mixed-form data tracks remain read-only. Their
payload geometry is not a 2,048-byte ISO sector stream, so flattening them through
the ISO rebuild path would destroy information even if their checksums could be
regenerated. Unsupported or malformed sector geometries are likewise refused before
committing any staged output.

## Maintenance verbs

- **defrag**: rebuilds a supported Mode-1 or Mode-2 Form-1 embedded ISO inside the
  fixed optical track, regenerating raw-sector integrity where necessary;
- **shrink**: only the existing single cooked Mode-1 creation profile is recreated
  smaller; raw/Mode-2 and multi-track images copy through unchanged because
  shrinking them would require changing optical/sector geometry the creator does
  not yet author explicitly;
- **purge**: rebuilds the selected supported ISO track empty while leaving all
  other tracks/sessions intact;
- **compact**: benefits from the defrag/shrink paths above;
- **wipe**: intentionally unsupported until a complete optical + filesystem free
  space map exists;
- **layout**: intentionally unsupported; no meaningful user-selectable CDI optical
  geometry has been defined, and adding a checkbox is not a format feature.
