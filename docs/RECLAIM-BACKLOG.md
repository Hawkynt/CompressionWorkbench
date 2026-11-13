# ♻️ Reclaim backlog

> Every format, method or capability the proof campaign removed, demoted or refused since
> 2026-10-01, with what it would take to bring it back for real. Snapshot of 2026-11-03; statuses as of 2026-11-06.

The proof campaign was right to delete what nothing outside this repository could confirm. This
page is the other half of that work: it lists each loss so that none of them stays lost by
default. An item comes back only with the same evidence any new format needs: a third-party
tool or real files, fixtures with provenance, and catalogue and README rows (see `AGENTS.md`,
"Sourcing an implementation").

Sources: `git log origin/main --since=2026-10-01`, the bodies of the open pull requests on that
date, `docs/catalogue/reference-formats.csv` and `docs/catalogue/GAPS.md`.

## 🧭 How to read it

- **Value**: how often the item turns up in real files. **H**: everyday files, or a format with a
  large installed base (Blizzard games, Blu-ray, NSIS installers, VM images). **M**: common inside a
  niche (forensics, firmware analysis, game repacks). **L**: historical or rare.
- **Path back**: the sourcing rung (1 = licence-compatible source to convert, 2 =
  licence-incompatible source as specification and oracle, 3 = published specification) and the
  oracle that would prove it.
- **Status**: `open`, `in progress`, `done (#PR)`, `blocked` (with the blocker), or `owned`
  (another agent or open pull request holds the format; not to be duplicated).

Items whose base change is still an open pull request are `owned` by that pull request until it
merges. They are listed so that the follow-up is not forgotten.

## 🔝 Backlog, by value

| # | Item | What was lost, and why | Path back | Value | Status |
| ---: | --- | --- | --- | :---: | --- |
| 1 | **MPQ: encrypted files and the PKWARE DCL, Huffman, ADPCM, sparse and LZMA sector masks** | #478: sectors in any mask but zlib or bzip2 were returned as compressed bytes, so every such mask, imploded files and encrypted files are now refused. That covers most Warcraft III maps, StarCraft and Diablo archives. | Rung 1: StormLib (MIT): `pklib` explode/implode, `huff.cpp`, `adpcm.cpp`, `sparse.cpp`. Oracle: StormLib built from source, both directions. The DCL codec also serves InstallShield 3 `.Z` (#543). | H | done (#671) |
| 2 | **NSIS: LZMA behind the x86 branch filter, the uninstaller payload, creation** | #479 made extraction real. Installers built with `SetCompressor /SOLID lzma` and the default x86 filter are refused, and the old writer was deleted (it wrote no real installer). | Reading: the 7z BCJ x86 filter, once #595 (liblzma conversion) merges. Writing: would need the NSIS stub (zlib licence) plus `makensis` as the oracle. | H | done (#699) |
| 3 | **UDF: VAT, sparable and metadata partitions** | Type-1 partition maps only. UDF 1.50+ media are not read: Blu-ray (metadata partition), DVD±RW (sparable) and packet-written CD-R (VAT). | Rung 3: ECMA-167 and OSTA UDF 2.60. Oracle: `mkudffs` (udftools) makes each layout and the Linux `udf` driver reads it. | H | done (#683) |
| 4 | **ZFS writer and in-place edits** | #459 replaced an invented pool layout with a real OpenZFS reader. The writer, adder, modifier and block mover were deleted, so ZFS is read-only. | Rung 2/3: OpenZFS on-disk specification (CDDL) as reference. Oracle: `zpool import` and `zdb` in WSL. Effort is very high. | H | open |
| 5 | **QCOW2: zstd clusters, extended L2, backing files, encryption** | Images that use any of them are refused (`Qcow2Structures.cs`). | Rung 3: `qcow2.txt` in QEMU. Oracle: `qemu-img` in WSL creates each variant and checks ours. | H | done (#679, #735) |
| 6 | **RAR: recovery records and volumes, dictionaries over 4 GB, RarVM** | Refused in #631 (no reference stream for algorithm version 1). | RAR agent. | H | owned (#631, #617) |
| 7 | **7z: PPMd coder wiring, kAdditionalStreamsInfo** | Known gaps in the conformance matrix. | 7z agent and codec audit agent. | H | owned (#619, #623) |
| 8 | **Inno Setup: encrypted files, 1.2.x, creation** | #498 made extraction real and removed the fake writer. | innoextract (zlib) for decryption; creation would need ISCC as oracle. | H | owned (#498) |
| 9 | **EWF: multi-segment E01 (.E02 onward), Ex01, L01** | Single-segment E01 only. | Rung 1: libewf (LGPL-3.0-or-later). Oracle: `ewfacquire`, `ewfexport` and `ewfverify` (ewf-tools) in WSL. | M | done (#694 multi-segment E01, #737 Ex01); L01/Lx01 blocked: no producer or sample |
| 10 | **FreeArc: tornado, ppmd, lzp, grzip, rep, delta, exe, 4x4, encryption** | #480 replaced the private layout with real FreeArc 0.67 framing. Only storing and LZMA decode. Default FreeArc modes chain rep, exe and delta in front of LZMA, so most real archives are refused. | Licence of the FreeArc sources to be confirmed (believed GPL, so rung 2). Oracle: `arc.exe` 0.67, which runs on Windows. Start with rep, delta and exe (they unlock the default modes), then tornado. | M | done (#709, #715, #717 to #724) |
| 11 | **LHA: self-extracting executables, LHARK `lk7`** | #508 made the legacy methods real. LHARK's `lk7` is refused and SFX `.exe` files are not opened. | Rung 1: lhasa (ISC) handles both, and its test suite carries the archives. | M | done (#674) |
| 12 | **SquashFS v1, v2, v3** | v4 only (`SquashFsSuperblock.cs`). Older firmware images use v2 and v3. | Rung 1: 7-Zip `SquashfsHandler.cpp` (LGPL-2.1-or-later) reads 1.x to 4.x. Oracles: 7-Zip and `unsquashfs` 4.x, which reads 1.x to 3.x. Samples: `mksquashfs` 3.x built from source. | M | done (#703) |
| 13 | **StuffIt X: method 7, Darkhorse, Deflate, Blend, preprocessors, encryption, writer** | #528 makes the reader real and deletes the envelope-only writer. | XADMaster has no method 7 decoder; it needs a spec or a sample-driven derivation with StuffIt as the oracle. | M | owned (#528) |
| 14 | **StuffIt classic/5: methods 8 and 14, encrypted forks** | Refused in #504. | XADMaster (LGPL) has method 14; method 8 and DES need samples. | M | owned (#504) |
| 15 | **APFS: encryption, snapshots, fusion containers** | Recorded as unsupported in #540. | Rung 3: Apple File System Reference. Oracle: `apfs-fuse` (GPL, oracle only). | M | owned (#540) |
| 16 | **UHARC: 0.3/0.4 archives** | #566 removed the invented layout; #614 reads real 0.6, RLE (`-mr`), LZ78 (`-mw`), PPM (`-mx`), UHARC 0.2 and a store/LZP writer followed; 0.4 store/LZP reading followed; 0.3 and 0.4 ALZ/PPM are refused. | Rung 2: `UHARC.EXE` 0.6b and `UHARC04.EXE` as oracles. | M | owned (#614) |
| 17 | **ZPAQ: encrypted (`-key`), multi-part and `-index` archives, append-version modify** | Refused in #463; modify rewrites a single version instead of appending one. | Rung 1: libzpaq (public domain). Oracle: zpaq 7.15. | M | done (#708, #716, #738) |
| 18 | **UFS2 and big-endian UFS writing** | #579 reads them and refuses edits. | Oracle: FreeBSD `fsck_ffs` (`fsck.ufs`, ufsutils) and the Linux `ufs` driver. | M | owned (#579, #583) |
| 19 | **ACE: ACE 2.0 compression modes in the writer, multi-volume extraction** | #488 writes ACE 1.0 only and lists volume members without extracting them. | acefile (BSD-2) and unace as oracles. Volumes can use the `ArchiveVolumes` joiner from #626 once it merges. | M | volumes done (#725); ACE 2.0 writer blocked (#729) |
| 20 | **ISO 9660: interleaved files** | Interleaved files throw (`IsoReader.cs`). | Rung 3: ECMA-119. Oracle: 7-Zip and the Linux `isofs` driver; samples are rare. | L | done (#730) |
| 21 | **Compact Pro, ALZip, EGG: remaining refusals** | Compact Pro: the LZH block boundary has no fixture (#497). ALZip: split archives and obfuscated deflate (#507). EGG: split archives (#513). | Samples from ALZip (split) and a long Compact Pro LZH stream; volume support via #626. | L | owned (#497, #507, #513) |
| 22 | **DiskDoubler: methods 2 to 5, 7, 8, 10, delta, DDAR/DDA2 archives** | #477 decodes 0, 1, 6 and 9 and refuses the rest. | XADMaster (LGPL) has Method 2 and DDn; the others need samples. | L | done (#731, #732) |
| 23 | **Now Compress writer** | Read-only (#511); no tool to verify a writer. | Needs Now Compress itself under emulation. | L | owned (#511) |
| 24 | **HA: Unix special records** | Listed but not extracted (#549). | `ha.exe` as oracle. | L | owned (#549) |
| 25 | **sfArk: Turbo method** | Implemented but unproven (#526). | Needs a Turbo sample. | L | owned (#526) |
| 26 | **KGB Archiver** | Never implemented; PAQ6-based. | Rung 2: KGB is GPL, PAQ6 too. Oracle: the `kgb` binary. | L | done (#734) |
| 27 | **MDC, PackDisk, xMash, xDisk, DCS, LhF, ZAP (Amiga)** | Deleted in #478 and #525: invented layouts. Every real one packs its tracks with XPK. | XPK arrives with #518. Then real samples are needed; none were found (Aminet and dexvert searched). unar compiles these libxad clients out. | L | blocked: XPK (#518) and samples |
| 28 | **CHM: sections other than MSCompressed, Intel E8 LZX** | Refused in #588; none of 76 real files uses them. | Needs a sample from HTML Help Workshop. | L | blocked: sample |
| 29 | **ZIP method 96 (WinZip JPEG)** | Refused (#561): no sample archive exists. | XADMaster has an LGPL decoder. Needs a WinZip-written sample. | L | blocked: sample |
| 30 | **PowerISO DAA 0x110, split and password images; GBI** | Refused in #512. | PowerISO for Linux as oracle, if it writes them. | L | open |
| 31 | **AFF: multi-file AFD and AFM** | Single-file `.aff` only (#491). | afflib-tools as oracle. | L | done (#714) |
| 32 | **Atari AHDI partition tables** | GEMDOS hard-disk partitions are not read: no AHDI root sector parser. | Rung 3: AHDI 3.00 notes. Oracle: GNU parted's `atari` label and the Linux `atari` partition parser. | L | done (#713) |
| 33 | **Apple Partition Map: create, add and remove entries** | Tables are read and edited in place; entries cannot be created. | Oracle: `pdisk` / `mac-fdisk` and the Linux `mac` partition parser. | L | done (#733) |
| 34 | **Writers for AXFS, JFFS1, Fossil, UNIX V6** | Read-only (#502, #523, #516, #594): nothing outside this package reads them. | AXFS: the out-of-tree kernel module. JFFS1: a Linux 2.4 kernel under QEMU. Fossil: plan9port. V6: SIMH. | L | owned (open PRs) / open (Fossil) |
| 35 | **Creating `.ecc`, `.fec`, `.ecsbx`, `.rev` and `.onepkg`** | Read-only (#606, #612, #616, #617, #565). | Oracles exist for the first three (dvdisaster, lziprecover, blkar). `.rev` belongs to the RAR agent; `.onepkg` needs OneNote. | L | owned (open PRs) |
| 36 | **Installers: Wise, Installer VISE, MoleBox, Clickteam, Setup Factory** | Never real here; the catalogue records why (#586). | VISE: rung 1 (unvise, viseunpack, both MIT). Clickteam: rung 1 (cicdec, BSD-3), but needs samples. Setup Factory: sfextract (MIT), of unclear lineage. Wise and MoleBox: licences to be researched. | M | VISE (#739), Clickteam (#740), Wise (#743), Setup Factory (#744) done; MoleBox open |
| 37 | **Wolf RPG Editor archives** | Not implemented (#574 notes). | Rung 1: GARbro's DxLib archive reader (MIT). Oracle: GARbro and the game's own `Game.exe`. | L | done (#742) |
| 38 | **FLEX: byte-exact file lengths** | Inherent to FLEX: it stores whole 252-byte records (#510). | Nothing to reclaim. | — | inherent |
| 39 | **PAQ8 (paq8l)** | The `Paq8` descriptor wrote an order-0 bit-tree coder under paq8l's `paq8l -N` header; paq8l decoded our files to wrong bytes and we decoded its files to wrong bytes without an error, so it was removed. | Rung 2: paq8l (GPL) as a description and oracle (`paq8l.exe`, which runs here); some 3,500 lines of models and a mixer whose C fallback fixes the arithmetic, all integer. | L | open |
| 40 | **SQX: audio (5), text/PPMd (6) and ultra (7) methods, AES encryption, volumes, recovery records** | The rewrite onto the real format refuses them; the former writer's encryption, volumes and recovery data were its own layouts. SpeedCommander and Squeez installers use ultra and PPMd. | Rung 1: Squeez-c (MIT) `ultra.c`, `ppm8.c`, `audio.c`; its ultra decoder does not finish the installers' executables, so those need another oracle. | M | open |

## 🔗 Related

- [`catalogue/GAPS.md`](catalogue/GAPS.md): what the reference tools know that we do not.
- `CODEC-PROOF-AUDIT.md` (arrives with #634): which codecs a third party has confirmed.
- [`LICENCE-PROVENANCE.md`](LICENCE-PROVENANCE.md): the sourcing rung of every converted file.
