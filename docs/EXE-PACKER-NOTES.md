# Executable-packer unpacking notes

Per-packer support levels and the handler inventory live in the package README,
[`Hawkynt.FileFormats.Archives/README.md`](../Hawkynt.FileFormats.Archives/README.md)
→ *Executable packer handlers*. This file keeps the evidence behind those levels:
what the [chesvectain/PackingData](https://github.com/chesvectain/PackingData) corpus
measures, the two measurement errors that moved the figure, and the analysis of the
packers still blocked at payload location — written down so the dead ends are not
walked twice.

## Compression cores available as building blocks

The packer cluster is unlocked by a small set of raw codecs, all clean-room:

- **aPLib** - `BB_Aplib` (`Compression.Core/Dictionary/Aplib`), a bit-exact
  `aP_depack` decoder. Core of FSG, PECompact, and RLPack. ASPack is often
  listed here too and does not belong: its stream is Huffman-coded, not aPLib.
  The older `FileFormat.ApLib`/`ApLibStream` is a separate, non-standard
  self-framed codec that round-trips only against itself and does not decode
  real packer output; `BB_Aplib` is the reference-compatible one.
- **ASPack LZ** - `AsPackLzDecoder` (`FileFormat.ExePackers`), an LZX-family
  LZ77 with per-block canonical Huffman codes over a 24-bit code space and three
  recency-addressed distances. Decode only; ASPack's own format, reconstructed
  from its stub.
- **NRV2B/D/E** - `BB_Nrv2b/d/e`, UPX and WinUpack core.
- **Upack range coder** - `WinUpackStream` (`FileFormat.ExePackers`), the
  LZMA-idiom binary range coder WinUpack actually uses. It is not NRV,
  despite the packer's `.Upack` sections sitting next to UPX in every
  taxonomy.
- **LZMA** - `BB_Lzma`, MEW / MPRESS / RLPack-LZMA.
- **Generic NRV PE** - `nrv_pe` fallback: carves PE sections and accepts a bare
  NRV2B/2D/2E stream only after it inflates to a plausible executable or text
  payload.

## Measured against a corpus

The levels below are per-packer judgements. This table is the other thing: what
happens when every sample of the
[chesvectain/PackingData](https://github.com/chesvectain/PackingData) corpus is
run through `ExecutablePackerHandlers.DetectBest` and unpacked — 130 samples per
packer, 2470 in all.

Two columns, because they answer different questions and only the second is
evidence. *Claimed* counts samples reaching `PayloadDecompressed` or better,
which is the unpacker's own opinion of itself. *Verified* counts samples where a
distinctive 32-byte run taken from the original actually appears in what came
back. A generic probe that inflates loader data scores the first and not the
second, so a wide gap between the columns is the signal to go looking.

Verification needs the pre-packing original, and only 1562 of the 2470 samples
have one in the corpus; the *Compared* column says how many that was per packer,
and *Claimed* is restricted to those so the two columns can be read against each
other. Detection is measured over all 130.

| Packer | Samples | Detected | Compared | Claimed | Verified |
|---|---|---|---|---|---|
| ASPack | 130 | 130 | 111 | 111 | 101 |
| BeRoEXEPacker | 130 | 130 | 127 | 127 | 123 |
| exe32pack | 130 | 126 | 1 | 1 | 1 |
| eXpressor | 130 | 130 | 110 | 110 | 57 |
| FSG | 130 | 128 | 106 | 106 | 100 |
| JDPack | 130 | 129 | 111 | 111 | 106 |
| MEW | 130 | 130 | 126 | 126 | 120 |
| Molebox | 130 | 130 | 104 | 104 | 100 |
| MPRESS | 130 | 129 | 119 | 119 | 113 |
| Neolite | 130 | 124 | 0 | 0 | 0 |
| NSPack | 130 | 130 | 1 | 1 | 0 |
| Packman | 130 | 130 | 125 | 125 | 35 |
| PECompact | 130 | 130 | 6 | 6 | 0 |
| PEtite | 130 | 129 | 55 | 55 | 55 |
| RLPack | 130 | 130 | 125 | 125 | 115 |
| UPX | 130 | 130 | 117 | 117 | 61 |
| WinUpack | 130 | 130 | 108 | 108 | 107 |
| Yoda's Crypter | 130 | 130 | 110 | 110 | 106 |
| Yoda's Protector | 130 | 130 | 0 | 0 | 0 |
| **Total** | **2470** | **2455** | **1562** | **1562** | **1300** |

Recognition is 99.4%. Of the 1562 claims that could be checked, 1300 (83%) carry
recognisable original code and 262 do not, and three packers own most of the
shortfall: Packman (125 claimed, 35 verified), UPX (117/61) and eXpressor
(110/57).

Read the gap carefully rather than as a bug count. The probe takes its 32-byte
run from a third of the way into the original *file*, and a decompressed payload
is the runtime memory *image* — so a sample can be unpacked correctly and still
fail the probe when that run lands in an import directory or a relocation block
the loader rebuilds instead of compressing.

Asking the question the other way round separates the two cases: is a
distinctive run of what *we* produced present in the original? Where it is, the
bytes are genuine and the probe simply sampled elsewhere.

| Packer | Verified | Plus genuine elsewhere | No original bytes at all |
|---|---|---|---|
| Packman | 35 | 65 | 25 of 125 |
| UPX | 61 | 28 | 28 of 117 |
| eXpressor | 57 | 27 | 26 of 110 |

So most of what the single probe counts against these three is partial recovery
rather than wrong recovery, and the number worth chasing is the last column: 79
samples, not the 262 the first column implies.

Two measurement errors are worth recording, because between them they moved the
figure by more than any unpacker change has.

The audit compared the largest artifact, and `payload_candidates/`,
`aplib_payload@` and `compressed_payload.bin` hold the *packed* bytes — a section
of the input, which can easily be larger than what came out of it. Comparing
those against the original scored misses on samples that had unpacked correctly.

Comparing only one artifact was the second, and the worse of the two. A packer
that chains streams hands the original back in pieces: eXpressor emits
`stream_000`, `stream_001`, `stream_002`, and the run being looked for is as
likely to be in the second as the first. Every failing eXpressor sample named
`stream_000` — which was the audit reading the first piece and calling the
recovery wrong.

Together the two moved eXpressor from 39 verified to 57 and UPX from 58 to 61,
while Packman went from 42 to 35. The corrections do not run one way, which is
the reason to make them rather than assume the flaw was flattering.

Byte-exact recovery of the pre-packing original is not the bar and no tool meets
it: `upx -d` returns 174,911 bytes for an original of 174,968 (95.4% identical),
because packing rebuilds the PE. Measuring against whole-file equality scores
every packer here at zero and distinguishes nothing, which is why it is not the
column.

Five packers decompress nothing: Neolite, NSPack, PECompact and Yoda's Protector
by not getting there at all, and exe32pack with a single sample. PECompact's 6
claims and NSPack's 1 verify at zero, so they are not partial successes.

What blocks PECompact is worth writing down, because it rules out the obvious
guess. Its first section opens with a dword that is a plausible uncompressed size
— 770,836 against a virtual size of 790,528 on one sample — so the payload is
found and framed correctly. But no codec here decodes it: aPLib, NRV2B/D/E and
LZMA were each tried at every start offset from 0 to 96, on several samples, and
none produced a cleanly-terminated expansion. The reason appears to be that the
payload is not one stream. Across 40 samples a `u16` at offset 6 takes only
512, 1,024, 2,048, 4,096 or 8,192 — block sizes, not a codec parameter — so what
follows is a series of compressed blocks and feeding the whole region to a
stream decoder cannot work whatever the codec is. Seventeen of those 40 instead
share one fixed pair of values at offsets 4 and 6, which is a second layout
rather than the same one varying. Both need reading before a decoder is written;
guessing a codec is what has already failed.

Neolite is stuck for an unrelated reason and wants a different first move. It
keeps the ordinary section names — `.text`, `.rdata`, `.data`, `.rsrc`,
`.reloc` — and `.text` opens with the loader rather than the payload: `push 0`,
`push 0`, `mov ecx, 0x45328c`, `call`. Probing the first section therefore only
ever reads stub code, which is why every codec is refused. Nor is the payload
simply elsewhere in the file as a dense blob: the highest entropy in any section
of the samples measured is 6.65 and most sit between 4 and 5, where compressed
data would be near 8. Whatever Neolite does is lighter than a single compressed
image, and the stub has to be read to find out what.

NSPack is stuck at a different point again, and its layout is the most regular
of the three. Every one of the 130 samples has exactly two sections, `nsp0` and
`nsp1` — checked across all of them, not sampled. `nsp0` is a couple of hundred
bytes of loader; `nsp1` holds everything else.

What stops a decoder is that `nsp1` is not one thing. On the sample measured it
runs in three parts:

- a structured table from the start of the section, entropy between 1.5 and 2.9
  and full of what read as addresses and flags;
- x86 code, a second stage of the loader living inside the payload section —
  `56 52 56 6a 04 68 00 01 00 00 52 ff 95 ec fc ff` is
  `push esi; push edx; push esi; push 4; push 0x100; push edx; call [ebp-0x314]`
  at `0x1C00` into the section, and it is still code at `0x1DC0`, where
  `ff 72 f2 c3 5d c2 08 00 6a 00 ff 95 f8 fc ff ff` reads as
  `push [edx-0x0e]; ret; pop ebp; ret 8; push 0; call [ebp-0x308]`;
- the compressed data, which holds above entropy 7.5 for the rest of the
  section.

The boundaries between the three are approximate. Entropy separates the table
from the rest cleanly, but it does not separate dense x86 from compressed data:
code here measures 5 to 6.5 against the payload's 7.9, and a threshold set low
enough to catch the transition also fires inside the code. The two offsets above
were confirmed by disassembling what is at them, which is the only reliable way
to tell those two apart.

Four more things hold across the corpus, counted rather than sampled:

| claim | samples |
|---|---|
| the entry point lies inside `nsp0` | 130 of 130 |
| `nsp0`'s virtual size is more than ten times its raw size | 130 of 130 |
| `nsp1` opens with four or more repeats of `0x10000` among its first `0x800` bytes | 125 of 130 |
| `nsp0` and `nsp1` raw ranges overlap in the file | 63 of 130 |

The first two together say what `nsp0` is: a destination. It is almost empty on
disk and large in memory — on one sample 233 bytes of file against 167,936 bytes
of image, where the pre-packing original is 174,968 — so the loader unpacks into
it, and the entry point sits there because that is where control ends up. The
repeating `0x10000` is a record table, each record pairing that constant with a
rising address.

What that record table actually is, is the resource directory. The values give
it away once they are read as a whole rather than scanned for sizes: `0x409` is
the English (US) language id, the `0x8000....` entries are resource subdirectory
offsets with the high bit set that marks them as such, and the triples of RVA,
size and codepage are `IMAGE_RESOURCE_DATA_ENTRY` records. NSPack leaves
resources uncompressed, as most packers do, so that icons and version
information keep working without the loader running.

That holds up when counted. Of the 102 samples that have a resource directory at
all, 97 give it an address inside `nsp0`'s virtual range — the unpacked image's
address space, where the resources will be once the loader has run — while the
bytes themselves sit at the front of `nsp1`'s raw data in the file.

Which is the answer to why probing the section head finds nothing: it is reading
a resource directory. And the size of that directory is in the PE data
directory, so it can be stepped over rather than guessed at. On the sample
measured the resources are 6,968 bytes, ending `0x1D38` into the file, after
which entropy climbs through the loader stage and settles above 7.5 for the
compressed data.

One hypothesis was tested here and is wrong, which is worth recording so it is
not tried twice. `nsp0`'s raw bytes are not the original PE's section table. On
one sample the string `.text` appears there followed by a plausible virtual size
and address, which reads convincingly; across all 130 samples, none contains
every section name of its own original, and the commonest case is one name out
of four or five — which is what coincidence looks like when the names are
`.text` and `.rdata`.

What is certain is that the payload does not start where the section starts, and
where it does start has to come out of that table rather than be guessed at —
which is why probing the section head finds nothing whatever codec is tried. The
table is the thing to read next.

Seven of Yoda's Crypter's 110 comparable samples are byte-identical to the original — the
packer left them alone, and no amount of unpacking will produce a difference.
They are counted here as failures because the handler does not notice a file is
unpacked and say so. That is worth fixing on its own, and it is the only place
in the corpus where it happens: every other packer's samples all differ from
their originals, which was checked rather than assumed.

UPX moved from 45 to 129 of 130 in this measurement's own history: the NRV2B
encoder and decoder had drifted into a private dialect that agreed with itself
and nothing else, and the PackHeader validator rejected any binary whose image
outgrew the file it came from. Round-trip tests could not see either.

MPRESS moved from 0 to 123 for a different reason: its payload was stock LZMA1
all along, but with the 13-byte container stripped, so nothing in the codebase
could be handed the stream. Of the 7 that remain, 6 are MPRESS 1.x samples,
which pack with another codec, and 1 is not packed at all — it carries neither
an `.MPRESS` section nor the MPRESS/MATCODE literal, so every 2.x sample in the
slice decompresses. All 130 are 32-bit; the x86 call-transform pass is applied
to 64-bit images unverified.
WinUpack moved from 0 to 130 of 130 the moment the assumption that it shared
UPX's NRV core was tested instead of inherited. Its loader stub is plain x86
sitting in the packed image, and reading it shows an LZMA-idiom range coder.
On the 108 corpus samples whose original is also in the corpus, 95.4% of the
mapped image comes back byte for byte and 99 of them reproduce `.text`
exactly; what is missing is the import directory and the base relocations,
which the stub rebuilds at run time and therefore never compressed.

## Out of scope by policy

`AtomPePacker`, a red-team AV-evasion PE loader, is deliberately not implemented;
the entry is recorded here so the omission is visible rather than silent.
