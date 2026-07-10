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
As of the current audit, the manifest has 104 packer entries; 43 are mapped to a
registered CW executable-packer handler, and 61 remain unmapped.

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

## Dataset packers

| Packer              | Level   | Core / notes |
|---------------------|---------|--------------|
| UPX                 | Unpack  | NRV2B/D/E + LZMA; full detect-to-decompress-to-memory-image-to-synthetic rebuild. |
| FSG                 | Locate* | `FSG!` marker and t/ta/a structural layouts are recognized; structural fixtures and the sampled corpus path emit payload candidates; synthetic aPLib-FSG fixtures unpack. |
| ASPack              | Locate  | Corpus sample is recognized and emits candidate payloads, but does not expose a clean bare aPLib stream. |
| PECompact           | Locate  | Corpus sample is recognized and emits candidate payloads; plug-in codec/transform recovery remains. |
| RLPack              | Locate  | Corpus sample exposes `.RLPack` as `compressed_payload.bin`; aPLib/LZMA transform recovery remains. |
| _(unnamed aPLib)_   | Unpack  | `aplib_pe` generic fallback: any PE whose section inflates to a clean aPLib stream. |
| _(unnamed NRV)_     | Unpack  | `nrv_pe` generic fallback: any PE whose section inflates as NRV2B/2D/2E to a plausible payload. |
| MEW                 | Unpack* | MEW section layout is recognized; the sampled corpus path inflates through managed generic payload recovery and emits `reconstructed/reconstructed.exe`. Other MEW variants fall back to payload location. |
| MPRESS              | Locate  | `.MPRESS1` / `.MPRESS2` payload sections emitted by the `mpress` handler; managed decompression/transform reversal remains. |
| NSPack              | Locate  | Named `nspack` handler emits `nsp1`/largest `nsp*` payload sections; managed decompression/transform recovery remains. |
| PEtite              | Locate  | `.petite` packer section is emitted as `compressed_payload.bin`; custom aPLib-ish recovery remains. |
| Themida             | Locate  | Named `themida` handler emits the `.boot` payload section when present, but static full unpack is not claimed. |
| Yoda-Crypter        | Locate  | Named `yodacrypter` handler emits the `yC` section as `compressed_payload.bin`; cryptor transform recovery remains. |
| WinUpack (Ultimate) | Locate  | `.Upack` virtual target plus raw payload section, and the Packing Box `PS...` three-section layout, emitted by the `winupack` handler; managed transform/decompression not yet recovered. |
| Neolite             | Locate* | Custom LZ payload section emitted by minor handler. *aPLib-mode payloads are caught by the generic aPLib fallback. |
| Packman             | Unpack  | `.PACKMAN` handler uses the shared aPLib PE pipeline and produces decompressed payload plus synthetic rebuilt PE for the corpus sample. |
| JDPack              | Locate  | `.jdpack` payload section emitted as `compressed_payload.bin`; custom LZ recovery remains. |
| Exe32pack           | Locate  | `.i` / `.f` / `.c` / `.v` / `.h` packer section emitted as `compressed_payload.bin`; custom LZ recovery remains. |
| EXpressor           | Locate  | Packer section emitted as payload artifact; custom LZ recovery remains. |
| BeRoEXEPacker       | Locate  | Packer section emitted as payload artifact; LZMA / LZBRR / LZBRS recovery remains. |
| Alienyze            | Locate  | Packer section emitted as payload artifact; transform recovery remains. |
| Amber               | Locate  | Reflective-loader payload section emitted as `compressed_payload.bin`; extraction, not decompression. |
| Enigma Virtual Box  | Unpack* | Named handler recognizes `.enigma1`/`.enigma2`; sampled corpus path inflates through managed aPLib recovery and emits `reconstructed/reconstructed.exe`. Real target remains bundled file-tree extraction. |
| Molebox             | Locate  | Bundler/virtualizer section payloads emitted; file-tree extraction remains. |
| Eronana Packer      | Locate  | Packer section emitted as payload artifact; transform recovery remains. |
| TELock              | Unpack* | TELock handler recognizes the blank entry-section layout; aPLib-mode corpus samples decompress/rebuild, other samples emit the protected payload section. |
| Yoda-Protector      | Locate  | Protector payload section emitted where present; dump/emulation recovery remains. |

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
| hXOR-Packer  | Locate | Release-generated PE fixtures are recognized by the hXOR stub marker plus appended `FIFA` payload record; CW emits the transformed payload and reports that Huffman/XOR reversal is not implemented yet. |
| Xor_Packer   | Unpack | .NET PE wrapper with appended Base64/XOR/Base64 settings; CW statically decodes the embedded PE and emits `reconstructed/reconstructed.exe`. Upstream source fixtures are covered. |
| SimpleDpack  | Locate | Release-generated PE64 fixtures are recognized by the `.dpack` section and SimpleDpack marker; CW emits the transformed section payload and reports that loader transform reversal is not implemented yet. |

## Manifest Gaps

The current Packing Box manifest audit reports these entries as not yet mapped
to a registered CW executable-packer handler:

```text
ACProtect, Aegis, AinEXE, Andromeda, APack, Armadillo,
AtomPePacker, AxProtector, BurnEye, CCG_Packer, Conficker, ConfuserEx, Crunch,
DarkComet, DotNetZ, Dragon_Armor, ElecKey, ELF-Cryptor, ELF-Packer, ELFuck,
Emotet, Enigma_Protector, EXE_Bundle, EXE_Stealth, Ezuri, Kovter,
Laturi, LM-X_License_Manager, M0dern_P4cker, MaskPE, MidgetPack, Morphine,
Muncho, NetCrypt, Obsidium, PackELF, Pakkero, Pakr, PatchELF, PE-Packer,
PELock, PErplex, PEShield, PESpin, PEzor, ProCrypt, Redhip,
RPCrypt, SEPacker, Shiva, SmartPacker, Squishy, SVKP, TheArk, Thinstall,
TrickBot, VProtect, Ward, Windows-PE-Packer, WWPack, Zprotect
```

## Demoscene compressing linkers

| Packer   | Level  | Core / notes |
|----------|--------|--------------|
| Crinkler | Detect | Descriptor emits `metadata.json`, `diagnostics.json`, and original-image artifacts only. Native decompression and memory-image reconstruction are not implemented yet. Crinkler links the final PE itself, so a byte-identical pre-Crinkler executable is not generally recoverable. |

## Notes on the hard cases

Virtualizers such as Themida, cryptors with anti-debug such as TELock and
Yoda-Protector, and reflective or bundler loaders such as Amber, Enigma, and
Molebox cannot in general be reduced to a static decompress. For those the
honest target is precise detection, payload or resource location and extraction
where a container is present, and diagnostics that state a dynamic dump or
emulation is required, never a false claim of a runnable rebuild.
