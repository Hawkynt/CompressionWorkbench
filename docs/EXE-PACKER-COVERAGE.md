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
  `aP_depack` decoder. Core of FSG, ASPack, PECompact, and RLPack.
  The older `FileFormat.ApLib`/`ApLibStream` is a separate, non-standard
  self-framed codec that round-trips only against itself and does not decode
  real packer output; `BB_Aplib` is the reference-compatible one.
- **NRV2B/D/E** - `BB_Nrv2b/d/e`, UPX and WinUpack core.
- **LZMA** - `BB_Lzma`, MEW / MPRESS / RLPack-LZMA.
- **Generic NRV PE** - `nrv_pe` fallback: carves PE sections and accepts a bare
  NRV2B/2D/2E stream only after it inflates to a plausible executable or text
  payload.

## Measured against a corpus

The levels below are per-packer judgements. This table is the other thing: what
happens when every sample of the
[chesvectain/PackingData](https://github.com/chesvectain/PackingData) corpus is
run through `ExecutablePackerHandlers.DetectBest` and unpacked — 130 samples per
packer, 2470 in all. "Decompressed" counts samples reaching
`PayloadDecompressed` or better.

| Packer | Samples | Detected | Decompressed |
|---|---|---|---|
| RLPack | 130 | 130 | 130 |
| UPX | 130 | 130 | 129 |
| Packman | 130 | 130 | 128 |
| MEW | 130 | 130 | 98 |
| PECompact | 130 | 130 | 6 |
| BeRoEXEPacker | 130 | 130 | 2 |
| FSG | 130 | 128 | 2 |
| exe32pack | 130 | 126 | 1 |
| ASPack | 130 | 130 | 0 |
| eXpressor | 130 | 130 | 0 |
| JDPack | 130 | 129 | 0 |
| Molebox | 130 | 130 | 0 |
| MPRESS | 130 | 129 | 0 |
| Neolite | 130 | 124 | 0 |
| PEtite | 130 | 129 | 0 |
| WinUpack | 130 | 130 | 0 |
| Yoda's Crypter | 130 | 130 | 0 |
| Yoda's Protector | 130 | 130 | 0 |
| **Total** | **2470** | **2465** | **496** |

Recognition is effectively complete at 99.8%; inflation is at 20%. The gap is
the honest shape of the *Locate* level — the payload is found and never
decompressed — and the table says so per packer rather than per hand-picked
sample.

Two caveats on reading it. Byte-exact recovery of the pre-packing original is
not the bar and no tool meets it: `upx -d` returns 174,911 bytes for an original
of 174,968 (95.4% identical), because packing rebuilds the PE. And a decompressed
payload is the runtime memory image, so it does not contain the original file's
bytes verbatim until the packer's filter is reversed as well.

UPX moved from 45 to 129 of 130 in this measurement's own history: the NRV2B
encoder and decoder had drifted into a private dialect that agreed with itself
and nothing else, and the PackHeader validator rejected any binary whose image
outgrew the file it came from. Round-trip tests could not see either.

## Dataset packers

| Packer              | Level   | Core / notes |
|---------------------|---------|--------------|
| UPX                 | Unpack  | NRV2B/D/E + LZMA; full detect-to-decompress-to-memory-image-to-synthetic rebuild. LZMA-mode payloads (method 14) are a bare stream sized by the PackHeader and still need a size-driven entry point; they report that rather than decoding. |
| FSG                 | Locate* | `FSG!` marker and t/ta/a structural layouts are recognized; structural fixtures and the sampled corpus path emit payload candidates; synthetic aPLib-FSG fixtures unpack. |
| ASPack              | Locate  | Corpus sample is recognized and emits candidate payloads, but does not expose a clean bare aPLib stream. |
| PECompact           | Locate  | Corpus sample is recognized and emits candidate payloads; plug-in codec/transform recovery remains. |
| RLPack              | Unpack  | Own container: the stub's `lea esi,[ebp+imm32]` addresses a `{sourceRva, destinationRva}` block table, one bare compressed stream per original section, in LZMA (lc=8/lp=0/pb=2, end-marker terminated) or aPLib. The stub's x86 call/jump filter is reversed with the per-file marker byte stored ahead of the table. All 130 corpus samples decompress; the sections come back as raw file bytes, minus the import thunks RLPack blanks and rebuilds at run time. |
| _(unnamed aPLib)_   | Unpack  | `aplib_pe` generic fallback: any PE whose section inflates to a clean aPLib stream. |
| _(unnamed NRV)_     | Unpack  | `nrv_pe` generic fallback: any PE whose section inflates as NRV2B/2D/2E to a plausible payload. |
| MEW                 | Unpack* | MEW section layout is recognized; the sampled corpus path inflates through managed generic payload recovery and emits `reconstructed/reconstructed.exe`. Other MEW variants fall back to payload location. |
| MPRESS              | Locate  | `.MPRESS1` / `.MPRESS2` payload sections emitted by the `mpress` handler; managed decompression/transform reversal remains. |
| NSPack              | Locate  | Named `nspack` handler emits `nsp1`/largest `nsp*` payload sections; managed decompression/transform recovery remains. |
| PEtite              | Locate  | `.petite` packer section is emitted as `compressed_payload.bin`; custom aPLib-ish recovery remains. |
| Themida             | Detect/Locate | Runtime protector. The `themida` handler emits the `.boot`/protected section as `protected_section_*.bin` when present; it never runs the generic aPLib/NRV probes and never claims a decompression (runtime-protector diagnostic). |
| Yoda-Crypter        | Locate  | Named `yodacrypter` handler emits the `yC` section as `compressed_payload.bin`; cryptor transform recovery remains. |
| WinUpack (Ultimate) | Locate  | `.Upack` virtual target plus raw payload section, and the Packing Box `PS...` three-section layout, emitted by the `winupack` handler; managed transform/decompression not yet recovered. |
| Neolite             | Locate* | Custom LZ payload section emitted by minor handler. *aPLib-mode payloads are caught by the generic aPLib fallback. |
| Packman             | Unpack  | `.PACKMAN` handler uses the shared aPLib PE pipeline and produces decompressed payload plus synthetic rebuilt PE for the corpus sample. |
| JDPack              | Locate  | `.jdpack` payload section emitted as `compressed_payload.bin`; custom LZ recovery remains. |
| Exe32pack           | Locate  | `.i` / `.f` / `.c` / `.v` / `.h` packer section emitted as `compressed_payload.bin`; custom LZ recovery remains. |
| EXpressor           | Locate  | Packer section emitted as payload artifact; custom LZ recovery remains. |
| BeRoEXEPacker       | Locate  | Packer section emitted as payload artifact; LZMA / LZBRR / LZBRS recovery remains. |
| Alienyze            | Locate  | Packer section emitted as payload artifact; transform recovery remains. |
| Amber               | Locate  | Reflective PE loader. Carves a plaintext embedded PE as `embedded_pe.bin` when the loader stores one in the clear, else locates the (XOR/RC4-obscured) reflective payload; extraction, not decryption — the key lives in the shellcode stub. |
| Enigma Virtual Box  | Unpack* | Named handler recognizes `.enigma1`/`.enigma2`; sampled corpus path inflates through managed aPLib recovery and emits `reconstructed/reconstructed.exe`. Real target remains bundled file-tree extraction. |
| Molebox             | Locate  | Bundler/virtualizer section payloads emitted; file-tree extraction remains. |
| Eronana Packer      | Unpack  | Static LZ77 + canonical-Huffman decoder validated byte-for-byte against a real packed sample; restores every stripped section and emits `reconstructed/reconstructed.exe` (RVA-mapped synthetic PE; the true OEP and import-directory RVA are reported in `metadata.json`). |
| TELock              | Detect/Locate | Runtime protector (anti-debug/virtualization). Recognized by the `tElock` literal or a blank entry-bearing last section (FSG-shaped images are excluded so they route to the FSG handler). Emits the protected body as `protected_section_*.bin`; never runs the generic aPLib/NRV probes and never claims a decompression. |
| Yoda-Protector      | Detect/Locate | Runtime protector. Emits the protected payload section where present; never claims a decompression (runtime-protector diagnostic; dump/emulation required). |

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
Yoda-Protector, and reflective or bundler loaders such as Amber, Enigma, and
Molebox cannot in general be reduced to a static decompress. For those the
honest target is precise detection, payload or resource location and extraction
where a container is present, and diagnostics that state a dynamic dump or
emulation is required, never a false claim of a runnable rebuild.
