# PECompact 2 (LZMA codec) on disk

PECompact 2 (Bitsum) publishes no container specification. This page records the layout
of executables packed with its LZMA codec, measured on the 130 PECompact samples of the
public [chesvectain/PackingData](https://github.com/chesvectain/PackingData) corpus
against the unpacked originals the same corpus ships in `Notpacked/`. Every constant
below was read from those (packed, original) pairs; nothing here is guessed.

`PeCompactExecutablePackerHandler` implements this page.

## What the packed file looks like

- The section table is rewritten: usually `.text`, `.rsrc` and sometimes `.reloc`. The
  first section's `PointerToRelocations` field holds the ASCII `PEC2NO` (or `PEC2`).
- `BaseOfCode` and `SizeOfCode` in the optional header keep the original values.
- The first section's raw data opens with the payload stream (below), and the loader's own
  code and data follow the stream inside the packed sections.

## The payload stream

At the file offset of RVA `baseRva` (the first section, RVA 0x1000 in every sample):

| Offset | Size | Field |
| ---: | ---: | --- |
| 0 | 4 | uncompressed size |
| 4 | 1 | LZMA properties byte, `(pb * 5 + lp) * 9 + lc` |
| 5 | 4 | LZMA dictionary size |
| 9 | … | LZMA1 range-coded data, no end marker |

The properties seen in the corpus are lc=2/lp=0/pb=0 (props 2), lc=2/pb=1 (47),
lc=4/pb=2 (94), lc=8/pb=2 (98) and lc=4/lp=4/pb=1 (85); lc=8 is common enough that a
decoder limited to lc+lp ≤ 4 rejects one sample in ten.

The stream holds the original sections back to back, in RVA order, each with its trailing
zero bytes removed. Sections that are pure BSS contribute nothing, and only part of the
resources is stored here (the rest stays uncompressed in the packed `.rsrc`).

## The entry sequence

At the entry point the packer writes 35 bytes:

```
B8 imm32                 ; imm32 = image base + RVA of the loader
50
64 FF 35 00 00 00 00
64 89 25 00 00 00 00
33 C0
89 08
"PECompact2" 00
```

These 35 bytes overwrite the payload stream at that position. Two placements occur:

- **At the start of the first section** (the entry point is the section start): the
  sequence covers the 9-byte stream header and the first 26 stream bytes.
- **At the original entry point** (inside the first section): the sequence covers 35 bytes
  in the middle of the stream. The original entry point therefore equals the packed one.

The covered bytes are stored elsewhere in the file; their RVA is in the data block (below).
Putting them back is required before the stream decodes.

## The parameter block and the data block

The `imm32` of the entry sequence, minus the image base, is an RVA 0x24 bytes past a
parameter block of nine dwords. The first two give:

| Dword | Meaning |
| ---: | --- |
| 0 | RVA of the data block |
| 1 | inflated size of the data block |

The data block is a standard aPLib stream (`BB_Aplib`) that inflates to exactly the
recorded size. Inside it, the table is a run of seven-dword records:

```
{ tag, 0xFFFF0000, f0, f1, f2, f3, type:u16, extra0:u8, extra1:u8 }
```

`tag` is a per-file 32-bit value; the first record of a group carries zeros in place of the
`tag, 0xFFFF0000` pair. Record types:

| Type | f0 | f1 | f2 | f3 | extras |
| ---: | --- | --- | --- | --- | --- |
| `0x0002` section | RVA | `baseOffset` + payload offset | stored size | 0 | 0, 0 |
| `0x0010` stream | `baseRva` | `baseOffset` | compressed size | uncompressed size | 0, 0 |
| `0x0008` filtered region | RVA | RVA | last opcode offset | 0 | call marker, jump marker |
| `0x0200` exception list | RVA of the list | 0 | 0 | 0 | opcode (`E8`/`E9`), marker |

The section records come first, highest RVA first; then the stream record; then the
filtered regions and exception lists. The two dwords in front of the first section record
are the **entry RVA** (where the entry sequence sits) and the **RVA of the covered bytes**.
`baseRva` and `baseOffset` were 0x1000 in every sample. The stream record's uncompressed
size equals the stream header's.

The part of the payload before the first section record's offset belongs at `baseRva`.

## The call/jump filter

Each filtered region (normally the code section; occasionally two regions) went through an
x86 branch filter. For every position `i` from 0 to `last opcode offset` inclusive, scanning
forward:

- `E8 <callMarker> b2 b3 b4` or `E9 <jumpMarker> b2 b3 b4`: the original operand is
  `((b2 << 16) | (b3 << 8) | b4) - i`, as a little-endian 32-bit value; continue at `i + 5`.
- anything else: continue at `i + 1`.

The markers are chosen per file and can be zero. Unlike most packers' filters, the
subtrahend is the opcode offset `i`, not `i + 5`.

An original `E8 <callMarker>` (or `E9 <jumpMarker>`) pair that is not a converted branch
would be misread by that rule. The packer stores such a pair as `00 00` (or lets an earlier
conversion's operand cover it) and lists its RVA in an exception list: a zero-terminated
array of `u32` RVAs at the record's RVA in the packed file. After the filter has been
reversed, the opcode and marker of the record are written back at each listed RVA.

## What does not come back

- Import tables: the packer rewrites them and rebuilds the import address table at run
  time. Executables whose import address table sits inside the code section (several
  Windows system binaries in the corpus) therefore differ there.
- Resources: partly kept uncompressed in the packed `.rsrc`.
- Relocations: reprocessed by the loader.

## Measured result

Against the corpus (`PeCompactHandlerTests.RealCorpus_*`, run with `CWB_PACKINGDATA`
pointing at a checkout), 127 samples readable on the test machine:

- 125 decompress. Two (`Hash.exe`, `ftp.exe`) keep the data block in a form that is not an
  aPLib stream (it opens with its own size, `0xEA0`, and continues with plain loader code);
  that form is not described here and those two are refused at detection level.
- Of the 107 samples with an original, the section holding the original entry point comes
  back byte-identical (trailing zero padding excluded) for 102. The other five are the two
  refused samples and `calc.exe`, `cmd.exe`, `dfrgui.exe`, whose import address table sits
  inside the code section.
- `.data` comes back byte-identical wherever the sample decompresses.
