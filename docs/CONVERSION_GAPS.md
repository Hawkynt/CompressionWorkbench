# Conversion Matrix — Known Gaps Worklist

Actionable backlog of currently-failing `(source -> target)` conversion pairs in the
data-driven conversion matrix (`Compression.Tests/ConversionMatrix/`). Each gap is
**quarantined** in the matrix via `KnownGapTargets` / `KnownGapPairs` so the suite is
green-with-honest-gaps: a quarantined pair that fails is downgraded to an
`Assert.Ignore`, a quarantined pair that **starts passing** fails as a *stale entry*
(so fixes stay enforced), and any **un-quarantined** failing pair still fails hard
(regression guard on the passing pairs).

Genuinely-impossible pairs (pseudo-archives / typed-input-only targets, un-synthesizable
sources) remain plain `Assert.Ignore`s and are **not** listed here.

## Baseline

Measured on the full grid (8 representative sources x all creatable targets):

| Outcome | Count |
|---|---|
| Passing pairs | 1440 |
| Ignored — genuinely-impossible | 424 |
| Ignored — known gap (quarantined here) | 561 |
| **Total grid cells (+ coverage report)** | **2425** |

## Bucket summary

| Bucket | Failing targets | Failing pairs |
|---|---:|---:|
| self-rejecting reader | 1 | 8 |
| single-payload/whole-image target | 23 | 176 |
| name/charset/size constraint | 11 | 88 |
| other | 30 | 241 |
| **Total** | **65** (+1 pair-specific) | **513** |

> Fixed and flipped to enforced-pass: `Svx8` (descriptor class renamed so the
> source-generated Format enum Id matches its registry Id), `Crate`, `FreeArc`,
> `Wad2`, `Zpaq` (self-rejecting reader / detection-collision fixes), and `Mfs1`
> + `Nsis` (now re-readable; reclassified as name-synthesizing so the matrix
> content-matches their folded/synthesized entry names). `CpcDsk` moved to the
> single-payload bucket: its reader has no filesystem layer.
>
> Also flipped to enforced-pass (24 pairs): `ExFat` and `Hpfs`, which never
> "emitted a whole-image" at all — their `.img` output was mis-detected as FAT
> because detection read only the first 512 bytes and `.img` was hardcoded to
> FAT, so the re-list ran the wrong reader; and `Rpm`, whose `List` returned a
> hardcoded `payload.cpio` placeholder while `Extract` fed the still-compressed
> payload to the cpio parser.

> Most gaps are **target-wide** (the target fails from every one of the 8 sources, so
> the failing-pair count is `targets x 8`). Pair-specific gaps are listed in their own
> section.

## Bucket: self-rejecting reader

Writer emits a file its own reader (or the converter's auto-detected reader) cannot re-read. The conversion produces output, but re-listing it throws. Fix: make each writer's output pass its own reader (correct magic / structure).

| Target | Reason |
|---|---|
| `HfsPlus` | writer output is re-detected as DMG, whose reader rejects it: 'missing koly trailer signature' |

## Bucket: single-payload/whole-image target

The target collapses an arbitrary file tree into a single stream or whole-image blob (one `FULL.x`, `disk.img`, `data`, `joined`, etc.), so the round-trip re-list sees fewer files than expected (often 0 or 1). These may be genuinely single-payload by design; where so, the long-term fix is to teach the writer a multi-file container layout or reclassify the pair as impossible.

| Target | Reason |
|---|---|
| `AndroidOta` | OTA update payload; writer emits a whole-image blob that re-lists as 0 files |
| `AndroidSparse` | Android sparse image; Create sparsifies a single raw image and re-lists as metadata.ini+image.raw, not a file tree |
| `Awb` | CRI AWB audio bank; writer collapses the tree to a single FULL.amr stream |
| `BcacheFs` | bcachefs image writer emits a whole-image (FULL.bcachefs+superblock.bin stub) not a file tree |
| `CpcDsk` | Amstrad CPC disk image; reader exposes raw 512-byte track/sector blocks (T00S0_C1…) with no AMSDOS/CP/M filesystem layer, so padded sectors can't content-match the payload |
| `DiskDoubler` | single-fork compressor; carries only one payload (lists 1 file) |
| `Ewf` | EnCase EWF (.E01) wraps raw media as opaque chunks; the reader surfaces section blobs (volume/sectors/table/...), not the original files |
| `Lrzip` | single-stream long-range compressor; one 'data' member only |
| `Mp3` | single audio stream; collapses tree to one FULL.mp3 |
| `Msa` | Atari ST disk image; writer emits one disk.st blob |
| `Psf` | PlayStation sound format; fixed header.bin+program.bin pair, not a tree |
| `Sparseimage` | Apple sparse disk image; one disk.img blob |
| `SplitFile` | byte-splitter; rejoins to a single 'joined' member, not a tree |
| `Srec` | Motorola S-record firmware pseudo-archive; writer re-encodes one flat image and re-lists as metadata.ini+firmware.bin, not a file tree |
| `StuffItX` | writer emits an image that re-lists as 0 files |
| `SysV` | System V FS image writer emits a whole-image (FULL.htfs) not a tree |
| `Umx` | Unreal package; writer emits a blob that re-lists as 0 files |
| `Wbn` | WebBundle writer collapses tree to a single FULL.wbn |
| `Wrapster` | Wrapster MP3 wrapper; one FULL.mp3 + 0/1 frame, not a tree |
| `xDisk` | Amiga xDisk image; writer emits one .xdsk blob |
| `xMash` | Amiga xMash image; writer emits one .xmsh blob |

## Bucket: name/charset/size constraint

Retro/constrained filesystems that mangle or synthesize entry names, or pad file content to a fixed record/block size, so the payload cannot be matched by verbatim name or byte-identical content. Fix: extend the matrix's name/size tolerance for these targets OR preserve names/sizes in the writer.

| Target | Reason |
|---|---|
| `Adfs` | Acorn ADFS; '.'-folding names (HELLO_TXT) break verbatim-name match (not in NameSynthesizing set) |
| `AppleDos` | Apple DOS 3.3 disk; name-synthesizing FS, payload not found by content (name+layout normalization) |
| `Bbc` | BBC Micro DFS disk; name-synthesizing FS, payload not found by content |
| `Cpm` | CP/M disk; 8.3-folding name-synth FS, payload not found by content |
| `Lif` | HP-71 LIF disk pads file content to a 256-byte record so bytes differ |
| `Ods1` | ODS-1 (Files-11) disk pads content to a 512-byte block so bytes differ |
| `ProDos` | ProDOS disk; name-synthesizing FS carried no files (name/size constraints reject the fixture) |
| `Rt11` | RT-11 disk pads content to a 512-byte block so bytes differ |
| `TrDos` | TR-DOS disk; name-synthesizing FS, payload not found by content |
| `Wad` | Doom WAD lump names are 8-char-truncated (HELLO.TX) so verbatim-name match fails |
| `ZxScl` | ZX-Spectrum SCL; name-synthesizing FS, payload not found by content |

## Bucket: other

Archive/format writers where the payload survives but entries are renamed (e.g. `entry_NNN.bin`, `record_NNNNN.bin`, `resource: <name>`) so the verbatim-name check fails, plus genuine content mismatches not explained by FS block padding. Fix: preserve original names on write, or relax name-matching for the target.

| Target | Reason |
|---|---|
| `Akb` | Koei AKB audio bank; entries renamed to entry_NNN.bin so verbatim-name match fails |
| `CramFs` | CramFs read-back returns 0-byte content for HELLO.TXT (decompression stub) |
| `Cso` | CSO compressed ISO; payload exposed as FULL.cso+block_* not original names |
| `Dcs` | DCS Amiga disk; exposed as track_NNN.raw not original names |
| `Dmg` | DMG read-back returns 512-byte padded content (block padding, content mismatch) |
| `Dtb` | Device-tree blob; names re-rooted under _root/ and de-extensioned (HELLO.bin) |
| `Esd` | ESD image; entries renamed to resource_NNNN.bin |
| `Fits` | FITS; payload exposed as hdu_* header/data members not original names |
| `G64` | G64 GCR disk; exposed as track_NN.bin not original names |
| `GameMaker` | GameMaker data.win; exposed as chunks/GEN8.bin not original names |
| `Ghost` | Norton Ghost image; exposed as partitionN.bin not original names |
| `InnoSetup` | Inno Setup installer; re-lists as the single .exe stub, names lost |
| `Lfd` | LucasArts LFD; entries renamed DATA.<stem> / RMAP.resource |
| `LhF` | LhF Amiga disk; exposed as track_NNN.raw not original names |
| `Lnk` | Windows .lnk; exposed as header.bin/linkinfo.bin not a file tree |
| `Macrium` | Macrium image; exposed as disk-image.raw + block-NN.$* not original names |
| `Mbox` | mbox mail; entries renamed message_NN.eml |
| `Mhk` | Mohawk archive; entries renamed tDAT_NNNN |
| `Mix` | Westwood MIX; entries renamed to 32-bit name-hash .bin |
| `Mo` | GNU gettext .mo; entries renamed NNNN_<stem>.txt |
| `Npy` | NumPy .npy; collapses to header.bin/array.bin (single array, names lost) |
| `Npz` | NumPy .npz; entries suffixed .npy (HELLO.TXT.npy) so verbatim-name match fails |
| `PackDisk` | PackDisk Amiga disk; exposed as track_NNN.raw not original names |
| `Paragon` | Paragon backup; exposed as chunk_NNNNNN.bin not original names |
| `Reiser4` | Reiser4 image; exposed as *_superblock.bin not original names (read-back stub) |
| `TfRecord` | TensorFlow TFRecord; entries renamed record_NNNNN.bin |
| `Tfc` | UE texture cache; entries renamed bundle_NNNNN.bin |
| `Warc` | WARC; entries listed as 'resource: <name>' so basename match fails |
| `Wim` | Windows Imaging; entries renamed resource_N |
| `Zap` | Zap Amiga disk; exposed as track_NNN.raw not original names |

## Pair-specific gaps

Targets that work from most sources but fail from a specific source.

| Source -> Target | Bucket | Reason |
|---|---|---|
| `SevenZip` -> `FatPlus` | other | FAT+ read-back truncates DATA.BIN to 14 B from a SevenZip source only (pair-specific content mismatch) |

---

*Regenerate the baseline with* `dotnet test --filter "FullyQualifiedName~ConversionMatrix"`.
*When a fix lands, the previously-quarantined pair will fail as a STALE entry — remove
it from both `KnownGapTargets`/`KnownGapPairs` and this document.*
