# 🧾 Format catalogue — gaps

> What 7-Zip, libarchive, XADMaster, binwalk, Universal Extractor 2, PRONOM and Wikipedia know about
> that this repository does not, does only partly, or only reads. Snapshot of 2026-10-19.

The data is in [`reference-formats.csv`](reference-formats.csv): one row per real-world format,
codec or filesystem, with the sources that list it, the descriptor or building-block ids that cover
it here, and a gap type. [`our-registry.csv`](our-registry.csv) is the census of the registries the
mapping was checked against. `CatalogueConsistencyTests` fails when the CSV names an id that no longer
exists. It warns when a capability letter has drifted from the live descriptor.

## 📊 Headline numbers

| Measure | Value |
| --- | ---: |
| Catalogue rows | 983 |
| … formats listed by at least one source | 525 |
| … registry ids no source lists (OURS-ONLY) | 458 |
| Source-listed rows mapped to something here | 288 / 525 (54.9 %) |
| … of rows listed by ≥ 2 sources | 122 / 138 (88 %) |
| … of rows listed by ≥ 3 sources | 66 / 67 (PEA is the one missing) |
| … of rows listed by ≥ 4 sources | 31 / 31 |

| Gap type | Rows | Meaning |
| --- | ---: | --- |
| `MISSING` | 237 | a source lists it; no descriptor or building block covers it |
| `READ-ONLY-BUT-SOURCES-WRITE` | 23 | we only read it; a source or the format's own tool writes it |
| `PARTIAL` | 43 | covered, but the reader handles only part of the format (see the table below) |
| `OK` | 222 | covered with no known gap against what the sources list |
| `OURS-ONLY` | 458 | in our registry, listed by none of the seven sources |

### Per source

"Mapped" means at least one of our ids covers the row, partial or not. "OK" excludes partial and
read-only rows.

| Source | Rows it lists | Mapped | % | OK |
| --- | ---: | ---: | ---: | ---: |
| 7-Zip (formats + codecs) | 96 | 89 | 92.7 | 70 |
| libarchive (formats + filters) | 28 | 27 | 96.4 | 22 |
| XADMaster / The Unarchiver | 126 | 98 | 77.8 | 80 |
| binwalk v3 | 79 | 48 | 60.8 | 35 |
| Universal Extractor 2 | 124 | 71 | 57.3 | 47 |
| PRONOM | 87 | 63 | 72.4 | 41 |
| Wikipedia | 266 | 156 | 58.6 | 114 |

The libarchive and 7-Zip lists are almost fully covered. The rows still open are libarchive's grzip,
plus seven 7-Zip rows: LVM, Android `super.img` (LP), AVB, TE, the Swap2/Swap4 filter, PKWARE strong
encryption and RAR 2.0 encryption. Most of the remaining gaps come from four groups:

- binwalk's vendor firmware headers;
- UniExtract's installer and game-engine families;
- Wikipedia's long tail of historical, mainframe and appliance filesystems;
- XAD's StuffIt and Amiga codecs.

## 🕳️ Missing, ranked

Ranking: two points per source that lists the format, plus a prevalence weight of 1–3 (3 means you
meet it in everyday files). The weights are a judgement call and are shown in the table. Ties are broken by source count, then by name. The first 40 rows:

| # | Format | Kind | Listed by | Weight | Why it matters |
| ---: | --- | --- | --- | :---: | --- |
| 1 | PEA (PeaZip) | archive | UniExtract, PRONOM, Wikipedia | 2 | the only missing format that three sources list; the [format is published](https://peazip.github.io/pea-file-format.html) |
| 2 | Android boot image | firmware | binwalk, UniExtract | 3 | in every Android firmware dump; the header is documented by AOSP |
| 3 | InstallShield (CAB v5+ / setup exe) | installer | UniExtract, PRONOM | 3 | one of the most common Windows installers of the 1990s–2010s |
| 4 | Apple Disk Copy 4.2 | disk image | PRONOM, Wikipedia | 2 | the classic Mac floppy image; small, fixed layout |
| 5 | ISZ (compressed ISO) | disk image | UniExtract, Wikipedia | 2 | UltraISO's native image format |
| 6 | KiriKiri XP3 | game | UniExtract, Wikipedia | 2 | a widespread visual-novel engine archive |
| 7 | Mozilla ARchive (MAR) | archive | PRONOM, Wikipedia | 2 | Firefox/Thunderbird update packages; documented |
| 8 | PHP archive (PHAR) | package | UniExtract, Wikipedia | 2 | PHP application bundles; the format is in the PHP manual |
| 9 | PowerISO DAA | disk image | UniExtract, Wikipedia | 2 | PowerISO's native image format |
| 10 | Windows Help (HLP) | document | UniExtract, PRONOM | 2 | the legacy WinHelp container with an LZ77 variant inside |
| 11 | DGCA | archive | UniExtract, Wikipedia | 1 | Japanese archiver |
| 12 | KGB Archiver | archive | UniExtract, Wikipedia | 1 | PAQ-based archiver; Wikipedia's writing table lists a writer |
| 13 | LogFS | filesystem | binwalk, Wikipedia | 1 | flash filesystem found in embedded images |
| 14 | MagicISO UIF | disk image | UniExtract, Wikipedia | 1 | MagicISO's native image format |
| 15 | PAK (NoGate ARC extension) | archive | XAD, Wikipedia | 1 | ARC superset; our `Pak` is the unrelated Quake PAK |
| 16 | sfArk | compression | UniExtract, Wikipedia | 1 | compressed SoundFont distribution format |
| 17 | CAB self-extractor (IExpress / MSCF SFX) | installer | UniExtract | 3 | `CabReader` requires `MSCF` at offset 0, so SFX-wrapped cabinets are not found |
| 18 | LUKS | disk image | binwalk | 3 | the standard Linux disk-encryption header |
| 19 | Linux LVM2 | partition table | 7-Zip | 3 | common on Linux servers; partition type 0x8E is recognised, the volumes are not |
| 20 | Linux kernel image (zImage/Image) | firmware | binwalk | 3 | every embedded Linux firmware |
| 21 | StuffIt Arsenic (method 15) | codec | XAD | 3 | the default method of StuffIt 5 and later, so most later `.sit` files cannot be extracted |
| 22 | VMFS | filesystem | Wikipedia | 3 | VMware datastores |
| 23 | WiX Burn bundle | installer | UniExtract | 3 | the bootstrapper behind many current MSI-based setups |
| 24 | Windows Update package (MSU) | installer | UniExtract | 3 | Windows update packages (CAB-based) |
| 25 | Wise Installer | installer | UniExtract | 3 | common 1990s–2000s Windows installer |
| 26 | AFF (v1–3) | disk image | Wikipedia | 2 | forensic image; only AFF4 is implemented (`Aff4`) |
| 27 | Android Archive (AAR library) | package | PRONOM | 2 | ZIP-based Android library package |
| 28 | Android Verified Boot (vbmeta) | firmware | 7-Zip | 2 | Android firmware metadata |
| 29 | Android logical partitions (`super.img`) | partition table | 7-Zip | 2 | the dynamic-partition container on every recent Android device |
| 30 | Apple Archive (`.aar`) | archive | Wikipedia | 2 | Apple's current archive format (`aa` tool) |
| 31 | Clickteam Install Creator | installer | UniExtract | 2 | Windows installer |
| 32 | Dreamcast GDI | disk image | UniExtract | 2 | GD-ROM dumps |
| 33 | ECM (Error Code Modeler) | disk image | UniExtract | 2 | the usual wrapper for CD images in retro distributions |
| 34 | InstallAware | installer | UniExtract | 2 | Windows installer |
| 35 | Internet Archive ARC (crawl) | archive | PRONOM | 2 | the predecessor of WARC; web archives still hold it |
| 36 | LTFS | filesystem | Wikipedia | 2 | the tape filesystem on LTO-5 and later |
| 37 | MIME HTML archive (MHT) | document | UniExtract | 2 | browser "single file" saves |
| 38 | Outlook Express mailbox (DBX) | document | UniExtract | 2 | legacy mail store |
| 39 | PKWARE Strong Encryption | encryption | 7-Zip | 2 | ZIP SES; 7-Zip lists it read-only |
| 40 | Safari webarchive | document | PRONOM | 2 | binary plist web archive |

Below the top 40, the remaining 197 `MISSING` rows are, by kind:

| Kind | Rows | Typical members |
| --- | ---: | --- |
| filesystem | 63 | historical, mainframe, appliance and flash filesystems (Wikipedia; SOS from XAD) |
| archive | 28 | historical archivers from Wikipedia (UC2, RK, YZ1, Zzip, …) and Amiga MDC / MXM |
| firmware | 27 | vendor firmware headers and obfuscations (binwalk) |
| installer | 18 | Windows installer builders (UniExtract) |
| disk image | 15 | flux and tape images (A2R, MOOF, SCP, Pasti, PZX, …) and Amiga disk packers |
| codec / encryption / filter | 19 | StuffIt and StuffIt X methods, DiskDoubler, Compact Pro, WinZip JPEG/WavPack, Swap2/4 |
| game | 10 | engine archives (UniExtract) |
| compression | 8 | pack, bzip 0.21, grzip, genozip, TERSE, … |
| other | 9 | recovery files, documents, executables |

Filter the CSV on `gap=MISSING` for the full list.

## ✍️ Read-only here, written elsewhere

Every catalogued row where a source records write support (7-Zip, libarchive, and Wikipedia's
"Comparison of file archivers" writing table) is creatable here. So this list comes from the
`native_writer` column: the format's own reference tool writes it, and we only read it.

| Format | Ours | Native writer | Listed by |
| --- | --- | --- | --- |
| Apple Partition Map | `PartitionedDisk` | pdisk / hdiutil | 7-Zip, PRONOM |
| CloneCD (CCD/IMG/SUB) | `CloneCd` | CloneCD | UniExtract, Wikipedia |
| GUID Partition Table (GPT) | `PartitionedDisk` | sgdisk / gdisk | 7-Zip, binwalk |
| MBR partition table | `PartitionedDisk` | fdisk / sfdisk | 7-Zip, binwalk |
| pcapng | `Pcapng` | dumpcap / Wireshark | binwalk, PRONOM |
| Acronis True Image | `AcronisTib`, `AcronisTibx` | Acronis True Image | Wikipedia |
| BeeGFS | `BeeGfs` | beegfs-setup-storage | Wikipedia |
| BitRock InstallBuilder | `BitRock` | InstallBuilder | UniExtract |
| GPFS / IBM Storage Scale | `Gpfs` | mmcrfs | Wikipedia |
| GlusterFS | `GlusterFs` | gluster volume create | Wikipedia |
| HxC Floppy Emulator (HFE) | `Hfe` | HxC Floppy Emulator tool | PRONOM |
| JuiceFS | `JuiceFs` | juicefs format | Wikipedia |
| Lustre | `Lustre` | mkfs.lustre | Wikipedia |
| MooseFS | `MooseFs` | mfschunkserver | Wikipedia |
| Parchive (PAR/PAR2) | `Par2` | par2cmdline | Wikipedia |
| Raw disk image | `PartitionedDisk` | dd | Wikipedia |
| ReFS | `Refs` | format /fs:refs (Windows) | Wikipedia |
| SQLite database | `Sqlite` | sqlite3 | UniExtract |
| Tahoe-LAFS | `TahoeLafs` | tahoe create-node | Wikipedia |
| Tux3 | `Tux3` | tux3 mkfs | Wikipedia |
| UPX-packed executable | `Upx` | upx | UniExtract |
| WOZ (Apple II) | `Woz` | Applesauce | PRONOM |
| pcap | `Pcap` | tcpdump | PRONOM |

`AcronisTib`, `Refs` and `TahoeLafs` declare `CanModify` without `CanCreate`: they can edit an
existing instance but cannot make a new one.

## 🧩 Partial readers

Found by reading the extract paths (`NotSupportedException` branches, method switches), not by
running real-world samples. The file named at the end of each row is where the limit sits.

| Format | Ours | Sources | What is missing |
| --- | --- | :---: | --- |
| 7z | `SevenZip` | 7 | Deflate64, ARM64, RISC-V, Zstd/Brotli/LZ4 coders and external header references unsupported (SevenZipReader.cs) |
| ISO 9660 (Joliet / Rock Ridge / El Torito) | `Iso` | 7 | Rock Ridge limited to NM names (no SL/PX/TF/CE, no zisofs); interleaved files throw (IsoReader.cs) |
| ZIP | `Zip` | 7 | methods xz (95), JPEG (96), WavPack (97) and PKWARE strong encryption unsupported (ZipReader.cs) |
| ARJ | `Arj` | 6 | method 4 is treated as stored, so method-4 members extract wrong (ArjReader.cs) |
| LHA / LZH | `Lzh` | 6 | lh2/lh3 are decoded with the lh5 decoder (approximation) (LhaReader.cs) |
| XAR (eXtensible ARchive) | `Xar` | 5 | bzip2 throws; lzma/xz members are returned raw (XarReader.cs) |
| StuffIt (classic + 5) | `StuffIt` | 4 | store and RLE only; LZW/Huffman/LZAH/Arsenic/DES methods throw (StuffItReader.cs) |
| StuffIt X | `StuffItX` | 4 | stored and deflate only; Brimstone/Cyanide/Darkhorse/Iron throw (StuffItXReader.cs) |
| zoo | `Zoo` | 4 | LZH method (2) unsupported (ZooReader.cs) |
| ALZip | `AlZip` | 3 | store and deflate only (AlZipReader.cs) |
| APFS | `Apfs` | 3 | no decmpfs (LZVN/LZFSE) compressed files, no encryption (ApfsReader.cs) |
| Compiled HTML Help (CHM) | `Chm` | 3 | content sections above 1 unsupported (ChmReader.cs) |
| FreeArc | `FreeArc` | 3 | only storing and lzma blocks decode; tornado/ppmd/lzp/grzip/rep/delta/exe and encryption are refused (FreeArcMethods.cs) |
| NSIS installer | `Nsis` | 3 | solid installers cannot be extracted per entry (NsisReader.cs) |
| NTFS | `Ntfs` | 3 | WOF (XPRESS/LZX) compressed files not decoded; LZNT1 is |
| QCOW / QCOW2 | `Qcow2` | 3 | no encryption, backing files, zstd clusters or extended L2 (Qcow2Structures.cs) |
| SquashFS | `SquashFs` | 3 | SquashFS v4 only; v1-v3 rejected (SquashFsSuperblock.cs) |
| UDF | `Udf` | 3 | type-1 partition maps only: no VAT/sparable/metadata partitions (UDF 1.50+ media) (UdfReader.cs) |
| VMDK | `Vmdk` | 3 | stream-optimized / compressed grains read raw; split extents and delta chains unsupported (VmdkReader.cs) |
| Windows PE | `Mz`, `PeResources` | 3 | archive view of resources/overlay, not a full PE section extractor |
| ZPAQ | `Zpaq` | 3 | reads only the stored blocks our own writer emits; ZPAQL-compressed blocks (every real zpaq level) throw (ZpaqReader.cs) |
| Btrfs | `Btrfs` | 2 | zlib/lzo/zstd compressed extents are skipped (BtrfsReader.cs) |
| Compact Pro | `CompactPro` | 2 | stored and RLE only; LZ+RLE and LZ+Huffman unsupported (CompactProReader.cs) |
| DiskDoubler | `DiskDoubler` | 2 | stored only; compressed members are returned as raw compressed bytes without an error (DiskDoublerReader.cs) |
| EnCase / Expert Witness (E01) | `Ewf` | 2 | single segment only (no .E02+), no L01 logical evidence, no Ex01 (EwfReader.cs) |
| PackIt | `PackIt` | 2 | PMag (stored) only; PMa4 Huffman members are returned raw (PackItReader.cs) |
| UHARC | `Uharc` | 2 | stored and LZP only (UharcReader.cs) |
| VHDX | `Vhdx` | 2 | differencing / partially-present blocks read as zeros (VhdxStream.cs) |
| Comic Book Archive | `Cbz`, `Cbr`, `Cb7` | 1 | .cbt (tar) and .cba (ACE) have no descriptor |
| DMS (Disk Masher) | `Dms` | 1 | NONE/SIMPLE/QUICK only; MEDIUM/DEEP/HEAVY1/HEAVY2 throw (DmsReader.cs) |
| EGG (ALZip) | `Egg` | 1 | store and deflate only; bzip2/LZMA/AZO, encryption and split archives unsupported (EggReader.cs) |
| Encoded text (uu/xx/b64/BinHex/yEnc) | `UuEncoding`, `B64Encoding`, `BinHex`, `YEnc` | 1 | no XXEncode descriptor |
| HA | `Ha` | 1 | stored only; HSC and ASC throw (HaReader.cs) |
| Inno Setup | `InnoSetup` | 1 | listing only; file extraction throws for every Inno version (InnoSetupReader.cs) |
| MS-DOS COMPRESS (KWAJ) | `Kwaj` | 1 | methods 2 (LZSS) and 3 (LZ+Huffman) throw (KwajStream.cs) |
| PAQ (paq6/7/8 family) | `Paq8` | 1 | a single PAQ8 variant (.paq8l); the other PAQ versions are mutually incompatible |
| RAR audio / VM filters | `Rar` | 1 | RAR3 filters matched to the six standard programs only; no general RarVM (Rar3Decoder.cs) |
| RPG Maker archives | `Rgss` | 1 | RGSSAD v1-3 only; MV/MZ asset encryption (.rpgmv*) has no descriptor |
| UFS / FFS (BSD), UFS2 | `Ufs` | 1 | UFS1 only; UFS2 (magic 0x19540119) is not recognised |
| Unity assets / bundles | `UnityBundle` | 1 | UnityFS bundles only; .unitypackage (tar.gz) and serialized-file internals are not handled |
| Windows thumbnail cache | `ThumbsDb` | 1 | Thumbs.db only; thumbcache_*.db has no descriptor |
| tar + compression (tar.gz/.bz2/.xz/.zst/.lz/.lz4/.br) | `TarGz`, `TarBz2`, `TarXz`, `TarZst`, `TarLzip`, `TarLz4`, `TarBr` | 1 | tar.Z / tar.lzma / tar.lzo have no compound descriptor (open via the stream format) |
| tar sparse files | `Tar` | 1 | GNU/PAX sparse members are not hole-reconstructed (TarReader.cs) |

### ⚠️ Found along the way (flagged, not fixed here)

- **Silent wrong output.** `DiskDoubler` and `PackIt` return compressed bytes as if they were the
  file. `Xar` does the same for lzma/xz members. `Btrfs` skips compressed extents, `Vhdx` reads
  unsupported block states as zeros, and `Vmdk` returns compressed grains raw. `Arj` treats method 4
  as stored, and `Lzh` decodes lh2/lh3 with the lh5 decoder. Each of these gives the caller wrong
  data instead of an error.
- **Capability claims against real files.** `Zpaq` declares `CanCreate | CanModify`, but does not
  read an archive written by the real tool. Its round-trip only proves that our writer and reader
  agree with each other. (`FreeArc` now reads FreeArc 0.67's archives and FreeArc accepts ours; see
  `docs/FREEARC-ON-DISK.md`.)
- **Stale comments.** The "(not supported)" comments in `ArcConstants.cs` and `ArjConstants.cs`
  describe methods the readers do decode.

## ➕ Ours only

These 458 registry ids are listed by none of the seven sources:

| Kind | Count | Typical members |
| --- | ---: | --- |
| archive | 185 | game archives (`Big`, `Gob`, `Grp`, `Hog`, `Mpq`, `Vpk`, `Wad`), document containers (`Docx`, `Odt`, `Epub`, `Xps`), scientific/data stores (`Hdf5`, `NetCdf`, `Parquet`, `Fits`), backup images (`Aomei`, `Macrium`, `Veeam`, `Paragon`), exe packers (`AsPack`, `Themida`, `VmProtect`) |
| media | 128 | audio codecs and containers, tracker modules, image and video demuxers |
| codec | 97 | building blocks no source names as a method (LZ77 family, entropy coders, context mixers) |
| filesystem | 28 | retro and niche filesystems (`TrDos`, `Ti99`, `Human68k`, `Pc98`, `Xenix`, `SysV`, `LittleFs`, `DoubleSpace`, `Stacker`, `Vdfs`) |
| compression | 20 | stream formats such as `Lzham`, `Lizard`, `Density`, `Yaz0`, `RefPack`, `Rnc`, `BriefLz` |

A few of these are plain ZIP/OLE2 variants that 7-Zip opens as ZIP or Compound, without naming them
separately: `Docx`, `Xlsx`, `Pptx`, `Odt`, `Epub`, `Xpi`, `Ipa`. They count as ours-only here only
because no source gives them a row of their own.

## 🖥️ The shell against 7-Zip File Manager, WinRAR and PeaZip

Evidence is from `Compression.NativeUI`, `Compression.Shell` and `Compression.CLI` as of this snapshot.

| Feature | 7-Zip FM | WinRAR | PeaZip | Here |
| --- | :---: | :---: | :---: | --- |
| Multi-volume create | ✓ | ✓ | ✓ | ✗ in the UI. The create dialog has no volume size. The library writes split WIM (`Swm`) and has `SplitFile` |
| Multi-volume extract | ✓ | ✓ | ✓ | ◐ `.001` sets via `SplitFile`; no RAR/ZIP/7z volume-set handling found |
| Encryption on create | ✓ | ✓ | ✓ | ✓ password, encrypt names (7z/RAR), AES choice (ZIP) |
| Password prompt on open/extract | ✓ | ✓ | ✓ | ✗ every GUI call passes `password: null`; encrypted archives report "no password" |
| Archive comments | ✓ (ZIP) | ✓ | ✓ | ✗ no view/edit |
| Test integrity | ✓ | ✓ | ✓ | ✓ Archive → Test (Ctrl+T), CLI `test` (no password, see above) |
| Repair / recovery records | ✗ | ✓ | ◐ | ✗ |
| Benchmark | ✓ | ✓ | ✓ | ✓ `BenchmarkWindow`, CLI `benchmark` (per building block) |
| SFX creation | ✓ | ✓ | ✓ | ✓ console or windowed stub per target runtime (7z/ZIP/RAR) |
| Hash / checksum tool | ✓ (CRC/SHA) | ◐ | ✓ | ✗ no command, although `Hawkynt.Algorithms.Checksums`/`Hashing` provide the algorithms |
| Archive conversion | ✗ | ✓ | ✓ | ✓ File → Convert, CLI `convert` / `convert-archive` / `convert-fs` |
| Drag-drop in and out | ✓ | ✓ | ✓ | ✓ lazy virtual files out, files and mail attachments in |
| Thumbnails / preview | ✗ | ◐ | ◐ | ✓ preview pane (image/text/hex), thumbnail view |
| Explorer context menu | ✓ | ✓ | ✓ | ✓ open, extract here/to folder, add to ZIP/7z |
| Edit inside archive (add/delete/rename) | ✓ | ✓ | ✓ | ✓ where the descriptor declares `CanModify`; rename in ZIP and several filesystem images |
| Search inside archives | ◐ | ✓ | ✓ | ✗ no filter box (strings search only in the analysis window) |
| Two-pane file manager | ✓ | ✗ | ✗ | ✗ single pane with tree and breadcrumb |
| Favorites / history | ✓ | ✓ | ✓ | ◐ back/forward history only |
| Scheduled backup / profiles | ✗ | ◐ | ✓ | ✗ (CLI `batch` templates only) |
| Secure delete | ✗ | ✓ | ✓ | ◐ wipe free space inside images only |
| Localization | ✓ | ✓ | ✓ | ✗ hard-coded English |
| Dark theme | ✗ | ✓ | ✓ | ✗ |
| Mount as drive | ✗ | ✗ | ✗ | ✓ Dokan / FUSE when present |
| Filesystem maintenance (defrag, shrink, compact) | ✗ | ✗ | ✗ | ✓ Defragment tab with block map |
| Analysis / carving / reverse engineering | ✗ | ✗ | ✗ | ✓ analysis window, CLI `analyze` / `carve` / `reverse-engineer` |

The reference-tool columns record common knowledge of each tool's current release, not a re-test;
◐ means partial or plugin-dependent. Largest UI gaps, in order:

1. a password prompt on open/extract;
2. multi-volume create/extract;
3. archive comments;
4. a hash tool;
5. search inside an archive;
6. repair/recovery records;
7. localization and a dark theme.

## 🔧 How the catalogue was built

- **Sources** (raw snapshots in [`sources/`](sources/)):
  - 7-Zip: the 7-zip.org format lists, plus the handler and codec file names in the `ip7z/7zip`
    source tree. R/W comes from the 7-zip.org pack/unpack lists.
  - libarchive: the README, plus the `archive_{read,write}_*` file names. tar and cpio variants are
    merged into one row each.
  - XADMaster / The Unarchiver: the theunarchiver.com list, plus the `XAD*Parser`/`*Handle` files and
    the libxad clients present in the `MacPaw/XADMaster` tree.
  - binwalk v3: the `src/signatures` and `src/extractors` file names and descriptions. `R` means an
    extractor exists, `D` means detection only. Pure crypto and text signatures (keys, hash
    constants, copyright strings) are excluded.
  - Universal Extractor 2: `docs/FORMATS.md`. The game archives it supports through plugins are not
    enumerated there.
  - PRONOM: DROID signature file V125, filtered by name. The DROID XML carries no format-type field,
    so the filter is keyword-based and may miss entries.
  - Wikipedia: List of archive formats; Comparison of file archivers (including its Writing table);
    List of file systems; Comparison of file systems; Disk image; Comparison of disc image software.
    A disk-image row is kept only when its extension appears in those articles. Network and pseudo
    filesystems are excluded unless we have a descriptor for them.
- **Merging.** [`sources/merge-aliases.txt`](sources/merge-aliases.txt) maps every source row to a
  catalogue row: LHA = LZH, compress = .Z = LZW, tar.gz/tgz = the tar compound row, all FAT variants
  = FAT. Rows mapped to `-` were dropped on purpose.
- **Caveats.**
  - The `aliases_extensions` column is a convenience. Some extension lists were filled in from
    general knowledge during collection, so they are not evidence.
  - The reference-tool R/W letters come only from where a source states them.
  - The `notes` on partial rows come from a static reading of our readers, not from real-world
    samples.
- **Gap rules**, applied in order:
  1. No mapped id → `MISSING`.
  2. Mapped, no source lists it → `OURS-ONLY`.
  3. No mapped id can create, and a source writes it or `native_writer` names a tool →
     `READ-ONLY-BUT-SOURCES-WRITE`.
  4. A reader limit is recorded in `notes` → `PARTIAL`.
  5. Otherwise → `OK`.
- **Keeping it current.**
  - Run `CatalogueConsistencyTests` with `CWB_WRITE_DOCS=1` to refresh `our-registry.csv` and the
    `ours_*` capability letters.
  - Mapping a new descriptor to a catalogue row is a hand edit of the `ours` and `gap` cells.
  - A new descriptor that matches no source row belongs in a new `OURS-ONLY` row.
