# Executable-packer unpacking coverage

Tracks CompressionWorkbench's unpacking support for the executable packers in the
[packing-box/dataset-packed-pe](https://github.com/packing-box/dataset-packed-pe/tree/main/packed)
corpus (25 Win32 PE packers). The target for each packer is a real
`IExecutablePackerHandler` registered in `Compression.Lib.ExecutablePackerHandlers`
that detects the packer and inflates its payload with our own building blocks,
not external-tool delegation.

The broader Packing Box inventory is audited from
[`docker-packing-box/src/conf/packers.yml`](https://github.com/packing-box/docker-packing-box/blob/main/src/conf/packers.yml)
by `DatasetProbe.PackingBoxPackersManifest_IsFetchableAndAuditsRegisteredHandlers`.
As of the current audit, the manifest has 104 packer entries; 44 are mapped to a
registered CW executable-packer handler, and 60 remain unmapped.

## Support levels

- **Unpack** - real handler: detects, locates the payload, and decompresses it
  with our building blocks. A natively-runnable rebuild is reported honestly as
  loader-version-specific where it is not guaranteed, exactly as the UPX handler
  does.
- **Locate** - handler or descriptor recognizes the packer and emits one or more
  payload artifacts, but decompression or transform reversal is not yet wired.
- **Detect** - handler or descriptor recognizes the packer and emits diagnostics
  and original-image artifacts only.

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
- **NRV2B/D/E** - `BB_Nrv2b/d/e`, UPX core.
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

## Dataset packers

| Packer              | Level   | Core / notes |
|---------------------|---------|--------------|
| UPX                 | Unpack  | NRV2B/D/E + LZMA; full detect-to-decompress-to-memory-image-to-synthetic rebuild. LZMA-mode payloads (method 14) are a bare stream sized by the PackHeader and still need a size-driven entry point; they report that rather than decoding. |
| FSG                 | Locate* | `FSG!` marker and t/ta/a structural layouts are recognized; structural fixtures and the sampled corpus path emit payload candidates; synthetic aPLib-FSG fixtures unpack. |
| ASPack              | Unpack  | Own LZ77+Huffman core (`AsPackLzDecoder`), not aPLib. The stub's region table drives an in-place restore of every packed section and the E8/E9 call filter is reversed; the resource directory ASPack relocates into its stub section is not put back, so the restored image is not byte-identical to the original file. |
| PECompact           | Locate  | Corpus sample is recognized and emits candidate payloads; plug-in codec/transform recovery remains. |
| RLPack              | Unpack  | Own container: the stub's `lea esi,[ebp+imm32]` addresses a `{sourceRva, destinationRva}` block table, one bare compressed stream per original section, in LZMA (lc=8/lp=0/pb=2, end-marker terminated) or aPLib. The stub's x86 call/jump filter is reversed with the per-file marker byte stored ahead of the table. All 130 corpus samples decompress; the sections come back as raw file bytes, minus the import thunks RLPack blanks and rebuilds at run time. |
| _(unnamed aPLib)_   | Unpack  | `aplib_pe` generic fallback: any PE whose section inflates to a clean aPLib stream. |
| _(unnamed NRV)_     | Unpack  | `nrv_pe` generic fallback: any PE whose section inflates as NRV2B/2D/2E to a plausible payload. |
| MEW                 | Unpack* | MEW section layout is recognized; the sampled corpus path inflates through managed generic payload recovery and emits `reconstructed/reconstructed.exe`. Other MEW variants fall back to payload location. |
| MPRESS              | Unpack  | 2.x: `.MPRESS1` carries a bare LZMA1 stream behind a 8-byte header of MPRESS's own (page count, packed size, lc/lp/pb), decoded through `BB_Lzma`'s raw entry point; the loader's E8/E9 operand transform is reversed, giving the original address space as `unpacked_image.bin`. Import-table and section-layout rebuild remain. 1.x packs with another codec and stays at payload-located. |
| NSPack              | Locate  | Named `nspack` handler emits `nsp1`/largest `nsp*` payload sections; managed decompression/transform recovery remains. |
| PEtite              | Unpack  | `petite` handler walks the block table behind the entry stub, replays the block-move records and inflates every block with the PEtite DEFLATE dialect (dynamic Huffman announced as block type 1); code blocks get the absolute-branch-target transform reversed. Imports, relocations and the original entry point are not rebuilt. |
| Themida             | Detect/Locate | Runtime protector. The `themida` handler emits the `.boot`/protected section as `protected_section_*.bin` when present; it never runs the generic aPLib/NRV probes and never claims a decompression (runtime-protector diagnostic). |
| Yoda-Crypter        | Locate  | Named `yodacrypter` handler emits the `yC` section as `compressed_payload.bin`; cryptor transform recovery remains. |
| WinUpack (Ultimate) | Unpack  | Upack's own LZMA-idiom range coder plus its call/jump filter, driven by the parameter block the loader stub reads out of the section table. Both container shapes decode — the compressed header that folds the PE headers into the DOS stub, and the plain-header one. The import directory and base relocations are rebuilt by the stub at run time, so the decompressed memory image does not carry them. |
| Yoda-Crypter        | Unpack  | Static stub walker. The packer does not compress — it leaves sections at their original offsets and runs a per-build byte cipher over the ones holding code or writable data. That cipher has to ship as executable instructions, so `YodaCrypterStub` reads it back off the two loop layers, replays it, takes the walker's own name-compare table as the skip list, and restores the original entry point from the slot the stub restores it from. 129 of 130 corpus samples decrypt; the one miss is a UPX-then-yC double pack that routes to the UPX handler on confidence. Byte-identical whole files stay out of reach: the packer overwrites the import directory with its own descriptor format and discards the Authenticode certificate and any overlay. |
| WinUpack (Ultimate) | Locate  | `.Upack` virtual target plus raw payload section, and the Packing Box `PS...` three-section layout, emitted by the `winupack` handler; managed transform/decompression not yet recovered. |
| Neolite             | Locate* | Custom LZ payload section emitted by minor handler. *aPLib-mode payloads are caught by the generic aPLib fallback. |
| Packman             | Unpack  | `.PACKMAN` handler uses the shared aPLib PE pipeline and produces decompressed payload plus synthetic rebuilt PE for the corpus sample. |
| JDPack              | Locate  | `.jdpack` payload section emitted as `compressed_payload.bin`; custom LZ recovery remains. |
| Exe32pack           | Locate  | `.i` / `.f` / `.c` / `.v` / `.h` packer section emitted as `compressed_payload.bin`; custom LZ recovery remains. |
| EXpressor           | Locate  | Packer section emitted as payload artifact; custom LZ recovery remains. |
| BeRoEXEPacker       | Unpack  | Entry-point stub parsed for its source/destination/filter immediates; the packed image body is decoded (LZMA in 129 of the 130 corpus samples, aPLib in the remaining one), the E8/E9 call filter is reversed and `reconstructed/reconstructed.exe` is emitted. Byte-identical recovery of the pre-pack *file* is impossible — the packer regenerates the headers and the resource section — but the recovered body matches the originals' section bytes at their virtual addresses (mean 99.5 % over the corpus, the residue being the rebuilt resources and the zeroed import thunks), and the recovered original entry-point RVA matches every sample's. |
| Alienyze            | Locate  | Packer section emitted as payload artifact; transform recovery remains. |
| Amber               | Locate  | Reflective PE loader. Carves a plaintext embedded PE as `embedded_pe.bin` when the loader stores one in the clear, else locates the (XOR/RC4-obscured) reflective payload; extraction, not decryption — the key lives in the shellcode stub. |
| Enigma Virtual Box  | Unpack* | Named handler recognizes `.enigma1`/`.enigma2`; sampled corpus path inflates through managed aPLib recovery and emits `reconstructed/reconstructed.exe`. Real target remains bundled file-tree extraction. |
| Molebox             | Unpack  | MoleBox 2.x keeps the original section table (names mangled, virtual addresses intact) and replaces each section's raw data. The loader's own chain is replayed: an LCG keystream over an LZSS'd loader blob, an IDEA-protected configuration record, then per-section IDEA decryption and zlib inflation. Every one of the 415 recoverable sections in the corpus comes back byte-identical to the pre-pack original, and the original entry point and image base are recovered in all 104 samples that have one to compare against. Sections the packer drops (raw-data-less `.reloc`/`BSS`/`.tls`) are gone for good, so 63 of the 104 are fully recoverable and the rest are recoverable except for those sections. No corpus sample carries a bundled file tree; the trailer that would hold one (magic `0xCAFEBABE`) is emitted when present. |
| Eronana Packer      | Unpack  | Static LZ77 + canonical-Huffman decoder validated byte-for-byte against a real packed sample; restores every stripped section and emits `reconstructed/reconstructed.exe` (RVA-mapped synthetic PE; the true OEP and import-directory RVA are reported in `metadata.json`). |
| TELock              | Detect/Locate | Runtime protector (anti-debug/virtualization). Recognized by the `tElock` literal or a blank entry-bearing last section (FSG-shaped images are excluded so they route to the FSG handler). Emits the protected body as `protected_section_*.bin`; never runs the generic aPLib/NRV probes and never claims a decompression. |
| Yoda-Protector      | Detect/Locate | Emits the protected payload section; never claims a decompression. Unlike the other entries in this row, the obstacle is not that execution is required — the stub was walked far enough to show the pipeline is static: the same layered polymorphic byte cipher as Yoda-Crypter (one key per section class, keyed off `.text`/`CODE`, `.data`/`DATA`, `BSS`/`.rdata`, `.idata`), and then an LZO1X stream behind a four-byte uncompressed-length prefix. What is not yet reversed is where the stub restores the original section names from: the packed section table is blank, and the walkers key off the original names. Until that is resolved the handler stays at payload location rather than guessing. |

## Additional Packing Box packers

These are public Packing Box entries outside the 25-family `dataset-packed-pe`
corpus slice currently used by the first-sample dataset probe.

| Packer       | Level  | Core / notes |
|--------------|--------|--------------|
| GZEXE        | Unpack | Shell wrapper with embedded gzip payload; CW statically inflates and emits `reconstructed/original_executable.bin`. External `gzexe` fixtures are covered. |
| BZEXE        | Unpack | Shell wrapper with embedded bzip2 payload; CW statically inflates and emits `reconstructed/original_executable.bin`. External `bzexe` fixtures are covered. |
| Alternate_EXE_Packer | Unpack | Packing Box marks this as a UPX 3.96 frontend; CW routes those outputs through the existing managed UPX unpack pipeline rather than registering a misleading duplicate detector. |
| Papaw        | Unpack | ELF wrapper with obfuscated XZ/LZMA2 payload; CW restores the appended original executable. External Papaw release fixtures are covered when tools are available. |
| GoPacker     | Unpack | Appended Zstandard executable payload; CW restores `reconstructed/original_executable.bin`. External GoPacker source fixtures are covered when Go is available. |
| Origami      | Unpack | .NET wrapper with XORed raw-Deflate managed payload; CW restores `reconstructed/original_assembly.bin`. External Origami source fixtures are covered when build prerequisites are available. |
| PyPePacker   | Unpack | Python zipapp PE wrapper; CW reads embedded literals, reverses EntropyEncoding v2, RC6-CBC and gzip, and emits `reconstructed/reconstructed.exe`. External PyPePacker-generated fixtures are covered. |
| PE-Toy       | Unpack | PE32 `.petoy` shell-section layout with aPLib payload; CW uses the shared aPLib PE pipeline and emits decompressed payload plus synthetic rebuilt PE for the documented layout. Upstream source fetch is covered. |
| Silent_Packer| Unpack | ELF64 XOR section-insertion wrapper; CW restores `.text` and entry point for the supported variant. External Linux release fixture test exists. |
| Huan         | Unpack | PE64 loader with encrypted `.huan` section; CW decrypts the embedded PE payload and emits `reconstructed/reconstructed.exe`. |
| hXOR-Packer  | Unpack | Locates the appended payload via the DOS-header `e_res2` insert offset and `FIFA` record, then statically reverses the stored (0) and single-byte-XOR (2, MSVCRT-`rand()`-keyed) transforms byte-for-byte and emits `reconstructed/reconstructed.exe`. The bespoke-Huffman modes (1, 3) stay at payload-located with a precise diagnostic. Validated against the real release binaries. |
| Xor_Packer   | Unpack | .NET PE wrapper with appended Base64/XOR/Base64 settings; CW statically decodes the embedded PE and emits `reconstructed/reconstructed.exe`. Upstream source fixtures are covered. |
| SimpleDpack  | Locate | Recognized by the `.dpack` section; emits the `.dpack` blob (`dpack_section.bin`) plus the stripped-section RVA/size targets read from the packed PE's own section table. The published v0.5.3 release binary's observed output did not match its documented per-section LZMA container, so no decode is fabricated against an unconfirmed format. |
| PE-Packer (czs108) | Locate | Recognized by the trailing `.shell` section with all original section names cleared; emits `shell_section.bin`. The additive (+0xCC) section cipher and compact import rewrite are documented, but the byte ranges/offsets they apply to live in a MASM-compiled shell (no prebuilt reference binary, no MASM/MSVC toolchain to pin them), so the transform is not reversed. |

## Manifest Gaps

The current Packing Box manifest audit reports these entries as not yet mapped
to a registered CW executable-packer handler:

```text
ACProtect, Aegis, AinEXE, Andromeda, APack, Armadillo,
AxProtector, BurnEye, CCG_Packer, Conficker, ConfuserEx, Crunch,
DarkComet, DotNetZ, Dragon_Armor, ElecKey, ELF-Cryptor, ELF-Packer, ELFuck,
Emotet, Enigma_Protector, EXE_Bundle, EXE_Stealth, Kovter,
Laturi, LM-X_License_Manager, MaskPE, Morphine,
Muncho, NetCrypt, Obsidium, PackELF, Pakkero, Pakr, PatchELF,
PELock, PErplex, PEShield, PESpin, PEzor, ProCrypt, Redhip,
RPCrypt, SEPacker, Shiva, SmartPacker, SVKP, TheArk, Thinstall,
TrickBot, VProtect, Windows-PE-Packer, WWPack, Zprotect
```

### AtomPePacker — out of scope by policy

`AtomPePacker` (a red-team AV-evasion PE loader) is deliberately **not** implemented.
Detecting and statically recovering a packer's payload is defensive analysis, but the
platform's automated real-time cyber safeguards block all work involving this specific
offensive-tooling project (verified across multiple models). This boundary is respected
rather than circumvented; the entry is recorded here transparently rather than silently
omitted. The Cyber Verification Program is the documented path for this class of work.

## Demoscene compressing linkers

| Packer   | Level  | Core / notes |
|----------|--------|--------------|
| Crinkler | Detect | Descriptor emits `metadata.json`, `diagnostics.json`, and original-image artifacts only. Native decompression and memory-image reconstruction are not implemented yet. Crinkler links the final PE itself, so a byte-identical pre-Crinkler executable is not generally recoverable. |
| squishy  | Locate | `squishy` handler recognizes the packed PE's single `logicoma`-named section and the "logicoma"/"squished by" credit text squishy embeds in the DOS-stub header, both confirmed against real output from the official squishy-0.1.3 (x86) and squishy-0.2.0 (x86-64) releases (`https://logicoma.io/squishy`); it locates and emits the compressed section as `compressed_payload.bin`. squishy is closed-source and, per its own release notes, codes the payload with an undocumented adaptive context-mixing model (PAQ/LZMA-inspired) plus a state-based disassembler transform — the same non-LZ, no-public-spec category as Crinkler and kkrunchy — so static decompression is not attempted. |

## Notes on the hard cases

Virtualizers such as Themida, cryptors with anti-debug such as TELock and
Yoda-Protector, and reflective or bundler loaders such as Amber and Enigma
cannot in general be reduced to a static decompress. For those the
Virtualizers such as Themida, cryptors with anti-debug such as TELock, and
reflective or bundler loaders such as Amber, Enigma, and Molebox cannot in
general be reduced to a static decompress. For those the
honest target is precise detection, payload or resource location and extraction
where a container is present, and diagnostics that state a dynamic dump or
emulation is required, never a false claim of a runnable rebuild.
