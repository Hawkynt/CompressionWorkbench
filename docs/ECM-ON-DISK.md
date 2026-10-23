# ECM (Error Code Modeler) — on-disk notes

ECM, by Neill Corlett, shrinks a raw CD image by dropping every field of a
CD-ROM sector that can be recomputed: the sync pattern, the mode byte, the
EDC and the ECC. There is no published specification. This page is the
specification `FileFormat.Ecm` is implemented from. It was written by reading
the GPL-3.0 reference tool (`ecm.c` from
[alucryd/ecm-tools](https://github.com/alucryd/ecm-tools), revision
`2765930b79fff478a6a584a554c719b8bf041422`) as a description of behaviour.
No code was taken from it. The same tool, built from that revision, is the
black-box oracle in `Compression.Tests/Ecm/EcmExternalConformanceTests.cs`.

The sector layouts and the EDC/ECC arithmetic come from ECMA-130 (CD-ROM) and
ECMA-168 / CD-ROM XA (Mode 2 forms). Both are already implemented in
`CdiCdSectorIntegrity`.

## Stream

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 4 | magic `45 43 4D 00` (`"ECM\0"`) |
| 4 | … | records, until the end marker |
| end | 4 | EDC of the whole decoded output, little-endian |

## Records

Each record starts with a variable-length type/count header. `n` is the
record count minus one, a 32-bit value:

- first byte: bits 0–1 = type, bits 2–6 = `n & 31`, bit 7 = another byte follows;
- each following byte: bits 0–6 = the next seven bits of `n` (least significant
  first), bit 7 = another byte follows.

`n = 0xFFFFFFFF` with type 0 is the end marker, always encoded as
`FC FF FF FF 3F`. A header whose value would need more than 32 bits is
corrupt.

| Type | Unit | Stored per unit | Rebuilt per unit |
| --- | --- | --- | --- |
| 0 | byte | the byte itself | 1 byte |
| 1 | Mode 1 sector | 3-byte address (raw bytes 12–14) + 2 048 user bytes | 2 352 bytes: sync, address, mode 1, data, EDC over bytes 0–2063, 8 zero bytes, P/Q parity using the real address |
| 2 | Mode 2 Form 1 sector | 4-byte subheader + 2 048 user bytes | 2 336 bytes: subheader twice, data, EDC over the 2 056 bytes from the first subheader, P/Q parity computed with a zeroed address/mode header |
| 3 | Mode 2 Form 2 sector | 4-byte subheader + 2 324 user bytes | 2 336 bytes: subheader twice, data, EDC over the 2 332 bytes from the first subheader |

Types 2 and 3 rebuild 2 336 bytes, not 2 352. The 16 bytes of sync and header
in front of a Mode 2 sector are carried as type 0 literals. The reference
encoder does this on purpose: after a Mode 2 sector, a sync pattern followed by
mode byte 2 is passed through as 16 literals.

The trailing EDC uses the CD-ROM EDC polynomial (ECMA-130 §14.3, reflected
`0xD8018001`, initial value 0), run over the decoded bytes in order.

## Encoder freedom

Any split of the input into records decodes to the same bytes. A run may be
cut into several records of the same type, and any sector may be stored as
literals. Our encoder classifies each offset in this order:

1. Mode 1, if 2 352 bytes are available, the sync and mode byte match, the
   reserved bytes are zero, and the EDC and P/Q parity agree with what would be
   regenerated.
2. Mode 2 Form 1 and then Form 2, if 2 336 bytes are available, the two
   subheader copies are equal, and the EDC (Form 1 also the parity) agrees.
3. Otherwise one literal byte.

It flushes a record once its stored payload reaches 4 MiB, so memory stays
bounded on any input size. The output is therefore not byte-identical to the
reference encoder's. Both tools decode each other's streams to the same image,
and the oracle tests check exactly that.

## Validation

The decoder refuses a stream whose trailing EDC does not match the rebuilt
data, as the reference tool does. A missing magic, a truncated record or an
over-long count is also an error.
