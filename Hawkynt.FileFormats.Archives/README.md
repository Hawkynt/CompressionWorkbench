# Hawkynt.FileFormats.Archives

[![NuGet](https://img.shields.io/nuget/v/Hawkynt.FileFormats.Archives.svg)](https://www.nuget.org/packages/Hawkynt.FileFormats.Archives/)
[![NuGet downloads](https://img.shields.io/nuget/dt/Hawkynt.FileFormats.Archives.svg)](https://www.nuget.org/packages/Hawkynt.FileFormats.Archives/)
[![License](https://img.shields.io/github/license/Hawkynt/CompressionWorkbench)](https://github.com/Hawkynt/CompressionWorkbench/blob/main/LICENSE)
[![CI](https://github.com/Hawkynt/CompressionWorkbench/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/Hawkynt/CompressionWorkbench/actions/workflows/ci.yml)
![Target](https://img.shields.io/badge/target-net10.0-blue)

> Pure-managed archive handling for .NET on top of `Hawkynt.Compression.Core`. The package claims the
> WHOLE domain â€” every compression stream, archive container, software package, document bundle,
> installer payload, game archive, backup image, executable packer and media container â€” not a
> selection of it. The support matrix below is the one ledger for that claim: every row is read from
> the format descriptor the package ships, and anything a row does not cover is a tracked gap.

## ğŸ“¦ Installation

```bash
dotnet add package Hawkynt.FileFormats.Archives
```

The package bundles the archive-domain `FileFormat.*` assemblies and takes `Hawkynt.Compression.Core` as its one NuGet dependency. No native `zlib`, `liblzma`, `libarchive` or `libbz2` is loaded at runtime.

## âœ¨ Features

- Compression-stream readers and writers for modern and historical formats, including the encodings (BinHex, MacBinary, uuencode/base64, yEnc).
- Archive enumeration, extraction, test, fresh creation and â€” for most containers â€” add/replace/remove on an existing archive.
- Maintenance verbs on the same surface: defragment, shrink, wipe unused space, optimize layout, reorder metadata.
- Software-package and installer inspection without executing the package or installer.
- Office, OpenDocument, e-book, mail and web bundles exposed through the same archive surface.
- Game, engine, console, Amiga and vintage archives beside the mainstream ZIP / TAR / 7z / RAR / CAB families.
- Backup and disk-image containers, executable images and resources, scientific data containers and media containers as pseudo-archives: one entry per addressable payload, track or stream.
- One `IArchiveFormatOperations` model for every container; `IStreamFormatOperations` for every single-stream codec.

## ğŸ§© Support matrix

| State | Meaning |
| --- | --- |
| **R** | List / extract / test only. |
| **WORM** | Read plus create a fresh archive; no edit of an existing one. |
| **R/W** | Read plus add / replace / remove on an existing archive. The edit may be byte-preserving in place or a verified extract â†’ edit â†’ re-create rebuild; both keep the result valid. |

Column legend: **Id** is the registry identifier (`FormatRegistry.GetById`, `cwb formats`). **Test** â€” the descriptor verifies checksums/structure (`CanTest`). **Maintenance** â€” the verbs the descriptor implements: `defrag` (`IArchiveDefragmentable`), `shrink` (`IArchiveShrinkable`), `wipe` (`IWipeEmpty` / `IArchiveLayoutMap`), `optimize` (`ILayoutOptimizable` or `SupportsOptimize`), `reorder` (`IFileInternalChunkMover`, moving container metadata such as MP4 `moov` or Matroska `Cues` in place). For media containers **Demux** is per-track extraction, **Mux** is building a container from elementary streams, **Remux / edit** is in-place relayout or editing. **Notes** name the deliberate subset or the naming quirk worth knowing; formats that do not preserve arbitrary entry names say so there.

Every State, Test, Maintenance, Compress/Decompress and Demux/Mux/Remux cell is derived from the descriptor's `Capabilities` and the interfaces its operations object implements; `Compression.Tests.Operations.ArchivesReadmeStateTests` fails when a cell disagrees with the built registry, so the table cannot drift from the code.

### ğŸ§µ Compression streams and encodings

| Format | Id | Extensions | Compress | Decompress | Optimize | Notes | Reference |
| --- | --- | --- | :---: | :---: | :---: | --- | --- |
| aPLib | `ApLib` | `.aplib` | âœ… | âœ… | âœ… | Standard 24-byte AP32 wrapper around a bare aPLib stream; older self-framed streams still read | [ibsensoftware.com](https://ibsensoftware.com/products_aPLib.html) |
| BALZ | `Balz` | `.balz` | âœ… | âœ… | âœ… | Flexible look-ahead parser; optimal mode keeps the greedy stream when it is smaller | [sourceforge.net](https://sourceforge.net/projects/balz/) |
| Base64 (uuencode wrapper) | `B64Encoding` | `.b64` `.base64` | âœ… | âœ… | â€” | libarchive-compatible `begin-base64` / `====` wrapper; not bare RFC 4648 Base64 | [GitHub](https://github.com/libarchive/libarchive/blob/master/libarchive/archive_write_add_filter_b64encode.c) |
| BCM | `Bcm` | `.bcm` | âœ… | âœ… | âœ… | Block-size search (16â€“128 KiB) | [GitHub](https://github.com/encode84/bcm) |
| [BinHex](https://en.wikipedia.org/wiki/BinHex) | `BinHex` | `.hqx` | âœ… | âœ… | â€” |  | [RFC](https://www.rfc-editor.org/rfc/rfc1741) |
| BriefLZ | `BriefLz` | `.blz` | âœ… | âœ… | âœ… | Reference-compatible blzpack stream; optimizer compares managed effort levels 1â€“10 | [GitHub](https://github.com/jibsen/brieflz) |
| [Brotli](https://en.wikipedia.org/wiki/Brotli) | `Brotli` | `.br` | âœ… | âœ… | âœ… |  | [RFC](https://www.rfc-editor.org/rfc/rfc7932) |
| BSC | `Bsc` | `.bsc` | âœ… | âœ… | âœ… | Managed BWT+MTF+RLE payload; optimizer searches block size/context order; full libbsc QLFC/LZP parity remains open | [GitHub](https://github.com/IlyaGrebnov/libbsc) |
| [bzip2](https://en.wikipedia.org/wiki/Bzip2) | `Bzip2` | `.bz2` `.bzip2` | âœ… | âœ… | âœ… |  | [sourceware.org](https://sourceware.org/bzip2/manual/manual.html) |
| cmix | `Cmix` | `.cmix` | âœ… | âœ… | âœ… |  | [GitHub](https://github.com/byronknoll/cmix) |
| [Unix compress (.Z)](https://en.wikipedia.org/wiki/Compress_(software)) | `Compress` | `.z` | âœ… | âœ… | âœ… |  | [pubs.opengroup.org](https://pubs.opengroup.org/onlinepubs/9699919799/utilities/compress.html) |
| CP/M Crunch | `Crunch` | `.cru` | âœ… | âœ… | âœ… |  | [Archive Team](http://fileformats.archiveteam.org/wiki/Crunch) |
| CSC | `Csc` | `.csc` | âœ… | âœ… | âœ… |  | [GitHub](https://github.com/fusiyuan2010/CSC) |
| Density | `Density` | `.density` | âœ… | âœ… | âœ… |  | [GitHub](https://github.com/k0dai/density) |
| Freeze | `Freeze` | `.f` `.freeze` | âœ… | âœ… | âœ… | Optimizer searches the parse strategy, the match-search depth and the position Huffman table | [Archive Team](http://fileformats.archiveteam.org/wiki/Freeze) |
| [gzip](https://en.wikipedia.org/wiki/Gzip) | `Gzip` | `.gz` `.gzip` | âœ… | âœ… | âœ… |  | [RFC](https://www.rfc-editor.org/rfc/rfc1952) |
| ICE Packer | `IcePacker` | `.ice` | âœ… | âœ… | âœ… |  | [Archive Team](http://fileformats.archiveteam.org/wiki/ICE) |
| KWAJ | `Kwaj` |  | âœ… vÚ±î¸Â¸­yêë¢°k¢G§¦*^Â)ÈRÂ)ÈRÂÂ´&6†—fRFVÕÒ†‡GG¢òöf–ÆVf÷&ÖG2æ&6†—fWFVÒæ÷&r÷v–¶’ôµt¢’À§ÂÆ—¦&B„Å£R’ÂÆ—¦&FÂæÆ—¦Â)ÈRÂ)ÈRÂ)ÈRÂÂ´v—D‡V%Ò†‡GG3¢òöv—F‡V"æ6öÒö–æ–¶WöÆ—¦&B’À§Â´Å£Bg&ÖUÒ†‡GG3¢òöVâçv–¶—VF–æ÷&r÷v–¶’ôÅ£Eò†6ö×&W76–öåöÆv÷&—F†Ò’’ÂÇ£FÂæÇ£FÂ)ÈRÂ)ÈRÂ)ÈRÂÂ´v—D‡V%Ò†‡GG3¢òöv—F‡V"æ6öÒöÇ£BöÇ£Bö&Æö"öFWböFö2öÇ£Eôg&ÖUöf÷&ÖBæÖB’À§Â´Å¤e4UÒ†‡GG3¢òöVâçv–¶—VF–æ÷&r÷v–¶’ôÅ¤e4R’ÂÇ¦g6VÂæÇ¦g6VÂ)ÈRÂ)ÈRÂ(	BÂVæ6ö×&W76VBæBÅ¥dâ&Æö6·2öæÇ“²F†Re4R÷Då26ö×&W76VB&Æö6²fÖ–Æ–W2&Ræ÷B–×ÆVÖVçFVBÂ´v—D‡V%Ò†‡GG3¢òöv—F‡V"æ6öÒöÇ¦g6RöÇ¦g6R’À§ÂÅ¤rÂÇ¦vÂæÇ¦vÂ)ÈRÂ)ÈRÂ)ÈRÂÂ´v—D‡V%Ò†‡GG3¢òöv—F‡V"æ6öÒöÖ&—G6æ&—FW2öÆ–&Ç¦r’À§ÂÅ¤„ÒÂÇ¦†ÖÂæÇ¦†ÖÂ)ÈRÂ)ÈRÂ)ÈRÂÂ´v—D‡V%Ò†‡GG3¢òöv—F‡V"æ6öÒ÷&–6†vVÃ““’öÇ¦†Õö6öFV2’À§Â´Ç¦—Ò†‡GG3¢òöVâçv–¶—VF–æ÷&r÷v–¶’ôÇ¦—’ÂÇ¦—ÂæÇ¦æÇ¦—Â)ÈRÂ)ÈRÂ)ÈRÂÂ¶æöævçRæ÷&uÒ†‡GG3¢ò÷wwrææöævçRæ÷&röÇ¦—öÖçVÂöÇ¦—öÖçVÂæ‡FÖÂ4f–ÆRÖf÷&ÖB’À§Â´Å¤Ô‚æÇ¦Ö•Ò†‡GG3¢òöVâçv–¶—VF–æ÷&r÷v–¶’ôÆV×VÂTS"SƒS“5¦—bTS"SƒS“4Ö&¶÷eö6†–åöÆv÷&—F†Ò’ÂÇ¦ÖÂæÇ¦ÖÂ)ÈRÂ)ÈRÂ)ÈRÂÂ³r×¦—æ÷&uÒ†‡GG3¢ò÷wwrãr×¦—æ÷&r÷6F²æ‡FÖÂ’À§Â¶Ç¦÷Ò†‡GG3¢òöVâçv–¶—VF–æ÷&r÷v–¶’ôÇ¦÷’ÂÇ¦÷ÂæÇ¦öÂ)ÈRÂ)ÈRÂ)ÈRÂÂ¶Ç¦÷æ÷&uÒ†‡GG3¢ò÷wwræÇ¦÷æ÷&rò’À§Â´Å¥5Ò†‡GG3¢òöVâçv–¶—VF–æ÷&r÷v–¶’ôÆV×VÂTS"SƒS“5¦—bTS"SƒS“57F2’ÂÇ§6ÂæÇ§6Â)ÈRÂ)ÈRÂ)ÈRÂÂµ$d5Ò†‡GG3¢ò÷wwrç&f2ÖVF—F÷"æ÷&r÷&f2÷&f3#3“R’À§Â´Ö4&–æ'•Ò†‡GG3¢òöVâçv–¶—VF–æ÷&r÷v–¶’ôÖ4&–æ'’’ÂÖ4&–æ'–Âæ&–ææÖ6&–æÂ)ÈRÂ)ÈRÂ)ÈRÂÂµ$d5Ò†‡GG3¢ò÷wwrç&f2ÖVF—F÷"æ÷&r÷&f2÷&f3sC’À§ÂÔ4ÒÂÖ6ÖÂæÖ6ÖÂ)ÈRÂ)ÈRÂ)ÈRÂ÷F–Ö—¦W"6V&6†W2ÆVv7’ÇW2&VGV6VBGW&&òôf7BôÖ–Bô†–v‚ôÖ‚ÖævVB&öf–ÆW2Â´v—D‡V%Ò†‡GG3¢òöv—F‡V"æ6öÒöÖF†–WV6†'F–W"öÖ6Ò’À§Âµ6´&—G5Ò†‡GG3¢òöVâçv–¶—VF–æ÷&r÷v–¶’õ6´&—G2’Â6´&—G6Âç6¶&—G6Â)ÈRÂ)ÈRÂ)ÈRÂÂ¶FWfVÆ÷W"æÆRæ6öÕÒ†‡GG3¢òöFWfVÆ÷W"æÆRæ6öÒöÆ–'&'’ö&6†—fRöFö7VÖVçFF–öâöÖ2÷FbôÖ÷&TÖ6–çF÷6…FööÆ&÷‚çFb’À§Âµ…Ò†‡GG3¢òöVâçv–¶—VF–æ÷&r÷v–¶’õ’Â†Âç†Æç†Â)ÈRÂ)ÈRÂ)ÈRÂÂ¶ÖGFÖ†öæW’ææWEÒ†‡GG3¢òöÖGFÖ†öæW’ææWBöF2÷æ‡FÖÂ’À§Â÷vW%6¶W"Â÷vW%6¶W&Âçç#Â)ÈRÂ)ÈRÂ)ÈRÂÂ´&6†—fRFVÕÒ†‡GG¢òöf–ÆVf÷&ÖG2æ&6†—fWFVÒæ÷&r÷v–¶’õ÷vW%6¶W"’À§ÂµÖEÒ†‡GG3¢òöVâçv–¶—VF–æ÷&r÷v–¶’õ&VF–7F–öåö'•÷'F–ÅöÖF6†–ær’ÂÖFÂçÖFÂ)ÈRÂ)ÈRÂ)ÈRÂÂ³r×¦—æ÷&uÒ†‡GG3¢ò÷wwrãr×¦—æ÷&r÷6F²æ‡FÖÂ’À§ÂV–6´Å¢ÂV–6´Ç¦ÂçV–6¶Ç¦Â)ÈRÂ)ÈRÂ)ÈRÂÆWfVÂæBÆWfVÂ3²F†R÷F–Ö—¦W"6V&6†W2F†RÆWfVÂæBF†RÆWfVÂÓ26V&6‚FWF‚Â·V–6¶Ç¢æ6öÕÒ†‡GG¢ò÷wwrçV–6¶Ç¢æ6öÒò’À§Â&Ve6²òe2Â&Ve6¶Âçg6ç&Vg6¶Â)ÈRÂ)ÈRÂ)ÈRÂ÷F–Ö—¦W"6V&6†W2F†R†—7F÷'’v–æF÷rÂF†RÖF6‚×6V&6‚FWF‚æBV–6²ÖF6‚–æFW†–ærÂ·v–¶’ææ–÷G6òæ÷&uÒ†‡GG¢ò÷v–¶’ææ–÷G6òæ÷&rõ&Ve6²’À§Â$ä2&õ6²Â&æ6Âç&æ6Â)ÈRÂ)ÈRÂ)ÈRÂÂ·6Vv&WG&òæ÷&uÒ†‡GG3¢ò÷6Vv&WG&òæ÷&rõ&ö%ôæ÷'F†Våö6ö×&W76–öâ’À§Â·'¦—Ò†‡GG3¢òöVâçv–¶—VF–æ÷&r÷v–¶’õ'¦—’Â'¦—Âç'¦ç'¦—Â)ÈRÂ)ÈRÂ)ÈRÂÂ·'¦—ç6Ö&æ÷&uÒ†‡GG3¢ò÷'¦—ç6Ö&æ÷&rò’À§Âµ6æ•Ò†‡GG3¢òöVâçv–¶—VF–æ÷&r÷v–¶’õ6æ’’Â6æ–Âç7¦ç6æ–Â)ÈRÂ)ÈRÂ)ÈRÂÂ´v—D‡V%Ò†‡GG3¢òöv—F‡V"æ6öÒövöövÆR÷6æ’ö&Æö"öÖ–âög&Ö–æuöf÷&ÖBçG‡B’À§Â7VVW¦R…5’Â7VVW¦VÂç7¦Â)ÈRÂ)ÈRÂ)ÈRÂÂ´&6†—fRFVÕÒ†‡GG¢òöf–ÆVf÷&ÖG2æ&6†—fWFVÒæ÷&r÷v–¶’õ5’À§Âµ5teÒ†‡GG3¢òöVâçv–¶—VF–æ÷&r÷v–¶’õ5tb’Â7vfÂç7vfÂ)ÈRÂ)ÈRÂ)ÈRÂeu2ô5u2õ¥u2VçfVÆ÷R÷F–Ö—¦W#²æòFrÖÆWfVÂ'6–ærÂ¶÷VâÖfÆ6‚æv—F‡V"æ–õÒ†‡GG3¢òö÷VâÖfÆ6‚æv—F‡V"æ–òöÖ—'&÷'2÷7vb×7V2Ó’çFb’À§Â5¢„Õ24ôÕ$U52Âµt¢ÖÆW72’Â7¤6ö×&W76ÂÂ)ÈRÂ)ÈRÂ)ÈRÂÂ´&6†—fRFVÕÒ†‡GG¢òöf–ÆVf÷&ÖG2æ&6†—fWFVÒæ÷&r÷v–¶’õ5¤DB’À§Â5¤DBÂ7¦FFÂÂ)ÈRÂ)ÈRÂ)ÈRÂÂ´&6†—fRFVÕÒ†‡GG¢òöf–ÆVf÷&ÖG2æ&6†—fWFVÒæ÷&r÷v–¶’õ5¤DB’À§Â·WVVæ6öFUÒ†‡GG3¢òöVâçv–¶—VF–æ÷&r÷v–¶’õWVVæ6öF–ær’ÂWTVæ6öF–ævÂçWVVçWVÂ)ÈRÂ)ÈRÂ(	BÂÂ·V'2æ÷Væw&÷Wæ÷&uÒ†‡GG3¢ò÷V'2æ÷Væw&÷Wæ÷&rööæÆ–æWV'2ó“c““““s“’÷WF–Æ—F–W2÷WVVæ6öFRæ‡FÖÂ’À§Âµ…¥Ò†‡GG3¢òöVâçv–¶—VF–æ÷&r÷v–¶’õ…¥õWF–Ç2’Â‡¦Âç‡¦Â)ÈRÂ)ÈRÂ)ÈRÂÂ·GV¶æ’æ÷&uÒ†‡GG3¢ò÷GV¶æ’æ÷&r÷‡¢÷‡¢Öf–ÆRÖf÷&ÖBçG‡B’À§Â·”Væ5Ò†‡GG3¢òöVâçv–¶—VF–æ÷&r÷v–¶’õ”Væ2’Â”Væ6Âç–Væ6çWVÂ)ÈRÂ)ÈRÂ(	BÂÂ·–Væ2æ÷&uÒ†‡GG¢ò÷wwrç–Væ2æ÷&r÷–Væ2ÖG&gBãã2çG‡B’À§Â–£Â–£Âç–£ç7§6Â)ÈRÂ)ÈRÂ)ÈRÂÂ·v–¶’çFö6¶FöÒæ6öÕÒ†‡GG3¢ò÷v–¶’çFö6¶FöÒæ6öÒ÷v–¶’õ”£’À§Â·¦Æ–%Ò†‡GG3¢òöVâçv–¶—VF–æ÷&r÷v–¶’õ¦Æ–"’Â¦Æ–&Âç¦Æ–&Â)ÈRÂ)ÈRÂ)ÈRÂÂµ$d5Ò†‡GG3¢ò÷wwrç&f2ÖVF—F÷"æ÷&r÷&f2÷&f3“S’À§Â¦Æ–ærÂ¦Æ–ævÂç¦Æ–ævÂ)ÈRÂ)ÈRÂ)ÈRÂÂ´v—D‡V%Ò†‡GG3¢òöv—F‡V"æ6öÒ÷&–6†÷‚öÆ–'¦Æ–ær’À§Âµ§7FæF&EÒ†‡GG3¢òöVâçv–¶—VF–æ÷&r÷v–¶’õ§7FB’Â§7FFÂç§7Fç§7FFÂ)ÈRÂ)ÈRÂ)ÈRÂÂµ$d5Ò†‡GG3¢ò÷wwrç&f2ÖVF—F÷"æ÷&r÷&f2÷&f3ƒƒs‚’À ¢222	ùyÎûˆò&6†—fR6öçF–æW'0 §Âf÷&ÖBÂ–BÂW‡FVç6–öç2Â7FFRÂFW7BÂÖ–çFVææ6RÂæ÷FW2Â&VfW&Væ6RÀ§ÂÒÒÒÂÒÒÒÂÒÒÒÂ¢ÒÒÓ¢Â¢ÒÒÓ¢ÂÒÒÒÂÒÒÒÂÒÒÒÀ§Â´4UÒ†‡GG3¢òöVâçv–¶—VF–æ÷&r÷v–¶’ô4Uò†6ö×&W76VEöf–ÆUöf÷&ÖB’’Â6VÂæ6VÂ"õrÂ)ÈRÂFVg&r+rv—RÂÂ´v—D‡V%Ò†‡GG3¢òöv—F‡V"æ6öÒöG&öRö6Vf–ÆR’À§Â¶f–õÒ†‡GG3¢òöVâçv–¶—VF–æ÷&r÷v–¶’ôf–ò’Âf–öÂæf–öÂtõ$ÒÂ)ÈRÂ(	BÂw&—FW27F÷&VBÖVÖ&W'2öæÇ“²F†RW"Öf–ÆRw¦—W‡FVç6–öâ—2&VB'WBæ÷Bw&—GFVâÂ´v—D‡V%Ò†‡GG3¢òöv—F‡V"æ6öÒö¶†öÇFÖâöf–ò’À§Â´Å¦—Ò†‡GG3¢òöVâçv–¶—VF–æ÷&r÷v–¶’ôÅ¦—’ÂÅ¦—ÂæÇ¦Â"õrÂ)ÈRÂFVg&r+rv—RÂÂ¶¶—ÆW"æ6öÕÒ†‡GG¢ò÷wwræ¶—ÆW"æ6öÒ÷v–â÷VæÇ¢ò’À§ÂÕ²„Ö–v6²’Â×¶Âæ×¶Â"õrÂ)ÈRÂFVg&r+rv—RÂÂ´&6†—fRFVÕÒ†‡GG¢òöf–ÆVf÷&ÖG2æ&6†—fWFVÒæ÷&r÷v–¶’ôÖ•6²’À§Â´%Ò†‡GG3¢òöVâçv–¶—VF–æ÷&r÷v–¶’ô%ò…Væ—‚’’Â&Âææ&æFV&Â"õrÂ)ÈRÂFVg&r+rv—RÂÂÚ±î¸Â¸­yêë¢°k¢G§¦*^[freebsd.org](https://www.freebsd.org/cgi/man.cgi?query=ar&sektion=5) |
| [ARC](https://en.wikipedia.org/wiki/ARC_(file_format)) | `Arc` | `.arc` | R/W | âœ… | defrag Â· wipe |  | [GitHub](https://github.com/hyc/arc) |
| [ARJ](https://en.wikipedia.org/wiki/ARJ) | `Arj` | `.arj` | R/W | âœ… | defrag Â· wipe |  | [arj.sourceforge.net](https://arj.sourceforge.net) |
| [Binary II](https://en.wikipedia.org/wiki/Binary_II) | `BinaryII` | `.bny` `.bqy` | R/W | âœ… | defrag Â· wipe |  | [mirrors.apple2.org.za](https://mirrors.apple2.org.za/ground.icaen.uiowa.edu/MiscInfo/Binary2/bin2.specs) |
| [CAB](https://en.wikipedia.org/wiki/Cabinet_(file_format)) | `Cab` | `.cab` | R/W | âœ… | defrag Â· wipe |  | [cabextract.org.uk](https://www.cabextract.org.uk/libmspack/) |
| [CB7](https://en.wikipedia.org/wiki/Comic_book_archive) | `Cb7` | `.cb7` | R/W | âœ… | defrag Â· wipe | 7z-backed comic book archive | [7-zip.org](https://www.7-zip.org/7z.html) |
| [CBR](https://en.wikipedia.org/wiki/Comic_book_archive) | `Cbr` | `.cbr` | R/W | âœ… | defrag Â· wipe | RAR-backed comic book archive | [rarlab.com](https://www.rarlab.com/technote.htm) |
| [CBZ](https://en.wikipedia.org/wiki/Comic_book_archive) | `Cbz` | `.cbz` | R/W | âœ… | defrag Â· wipe | ZIP-backed comic book archive | [pkware.cachefly.net](https://pkware.cachefly.net/webdocs/casestudies/APPNOTE.TXT) |
| [CHM](https://en.wikipedia.org/wiki/Microsoft_Compiled_HTML_Help) | `Chm` | `.chm` | R/W | âœ… | defrag Â· wipe |  | [cabextract.org.uk](https://www.cabextract.org.uk/libmspack/) |
| [Compact Pro](https://en.wikipedia.org/wiki/Compact_Pro) | `CompactPro` | `.cpt` | R/W | âœ… | defrag Â· wipe |  | [GitHub](https://github.com/MacPaw/XADMaster) |
| [CPIO](https://en.wikipedia.org/wiki/Cpio) | `Cpio` | `.cpio` | R/W | âœ… | defrag Â· wipe | Reads and writes all four header variants â€” binary (both byte orders), `odc`, `newc` and `crc`, the last with its payload checksum verified; the `Format` option picks one and edits keep the archive's own | [pubs.opengroup.org](https://pubs.opengroup.org/onlinepubs/9699919799/utilities/pax.html) |
| [DAR (Disk ARchive)](https://en.wikipedia.org/wiki/Dar_(disk_archiver)) | `Dar` | `.dar` | R | âœ… | â€” |  | [dar.linux.free.fr](http://dar.linux.free.fr) |
| DCS (Amiga) | `Dcs` | `.dcs` | WORM | âœ… | defrag | Whole-disk archiver: entries are track_NNN.raw | [Aminet](https://aminet.net) |
| [DiskDoubler](https://en.wikipedia.org/wiki/DiskDoubler) | `DiskDoubler` | `.dd` `.sea` | WORM | â€” | wipe | Single-fork compressor: one payload per file | [GitHub](https://github.com/MacPaw/XADMaster) |
| [DMS](https://en.wikipedia.org/wiki/Disk_Masher_System) | `Dms` | `.dms` | WORM | âœ… | defrag |  | [GitHub](https://github.com/markrabjohn/xDMS) |
| [EGG (ALZip)](https://en.wikipedia.org/wiki/EGG_(file_format)) | `Egg` | `.egg` | WORM | âœ… | defrag |  | [GitHub](https://github.com/alkegi/docs/blob/master/egg.md) |
| [ESD](https://en.wikipedia.org/wiki/Windows_Imaging_Format) | `Esd` | `.esd` | WORM | âœ… | wipe | Solid LZMS WIM; created images carry a metadata resource but entries re-list as resources | [Microsoft Learn](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/wim-and-esd-windows-image-files-overview) |
| [FreeArc](https://en.wikipedia.org/wiki/FreeArc) | `FreeArc` | `.arc` | R/W | âœ… | defrag Â· wipe |  | [GitHub](https://github.com/Bulat-Ziganshin/FA) |
| HA | `Ha` | `.ha` | R/W | âœ… | defrag Â· wipe |  | [Archive Team](http://fileformats.archiveteam.org/wiki/HA) |
| [IFF CDAF](https://en.wikipedia.org/wiki/Interchange_File_Format) | `IffCdaf` | `.cdaf` | R/W | âœ… | defrag Â· wipe |  | [Aminet](https://aminet.net) |
| [LBR](https://en.wikipedia.org/wiki/LBR_(file_format)) | `Lbr` | `.lbr` | R/W | âœ… | defrag Â· wipe |  | [gaby.de](http://www.gaby.de/cpm/manuals/archive/lbr.txt) |
| LhF (LhFloppy) | `LhF` | `.lhf` | R/W | âœ… | defrag Â· wipe | Whole-disk archiver: entries are track_NNN.raw | [Aminet](https://aminet.net) |
| [lrzip](https://en.wikipedia.org/wiki/Rzip#lrzip) | `Lrzip` | `.lrz` | WORM | âœ… | defrag | LZMA-wrapped subtype only; other lrzip subtypes are rejected; single data member | [GitHub](https://github.com/ckolivas/lrzip) |
| Lynx (Commodore) | `Lynx` | `.lnx` | R/W | âœ… | defrag Â· shrink Â· wipe | Stored entries only | [Archive Team](http://fileformats.archiveteam.org/wiki/Lynx_(Commodore_64)) |
| [LHA / LZH](https://en.wikipedia.org/wiki/LHA_(file_format)) | `Lzh` | `.lzh` `.lha` | R/W | âœ… | defrag Â· wipe |  | [GitHub](https://github.com/jca02266/lha) |
| [LZX (Amiga)](https://en.wikipedia.org/wiki/LZX) | `LzxAmiga` | `.lzx` | R/W | âœ… | defrag Â· wipe |  | [Aminet](https://aminet.net) |
| [mtree](https://man.freebsd.org/cgi/man.cgi?query=mtree&sektion=5) | `Mtree` | `.mtree` | WORM | âœ… | â€” | Filesystem metadata manifest; does not embed file bodies, so CWB does not dereference `contents=` host paths | [FreeBSD mtree(5)](https://man.freebsd.org/cgi/man.cgi?query=mtree&sektion=5) |
| [NuFX / ShrinkIt](https://en.wikipedia.org/wiki/ShrinkIt) | `NuFx` | `.shk` `.sdk` `.bxy` | R/W | âœ… | defrag Â· shrink Â· wipe |  | [nulib.com](https://nulib.com/library/FTN.e08002.htm) |
| PackDisk (Amiga) | `PackDisk` | `.pdsk` | WORM | âœ… | defrag | Whole-disk archiver: entries are track_NNN.raw | [Aminet](https://aminet.net) |
| PackIt | `PackIt` | `.pit` | R/W | âœ… | defrag Â· wipe |  | [GitHub](https://github.com/MacPaw/XADMaster) |
| [RAR](https://en.wikipedia.org/wiki/RAR_(file_format)) | `Rar` | `.rar` | R/W | âœ… | wipe | v1â€“v5 readers; creation and edits emit RAR4/RAR5 without claiming WinRAR encoder parity | [rarlab.com](https://www.rarlab.com/technote.htm) |
| [7z](https://en.wikipedia.org/wiki/7z) | `SevenZip` | `.7z` | R/W | âœ… | defrag Â· wipe |  | [7-zip.org](https://www.7-zip.org/7z.html) |
| [SHAR](https://en.wikipedia.org/wiki/Shar) | `Shar` | `.shar` `.sh` | R/W | âœ… | defrag | Add appends in place; Remove re-emits the script from the survivors | [gnu.org](https://www.gnu.org/software/sharutils/) |
| [Spark (RISC OS)](https://en.wikipedia.org/wiki/ARC_(file_format)) | `Spark` | `.spk` `.spark` | R/W | âœ… | defrag Â· wipe |  | [Archive Team](http://fileformats.archiveteam.org/wiki/Spark) |
| [Split File (.001)](https://en.wikipedia.org/wiki/File_spanning) | `SplitFile` | `.001` | WORM | âœ… | â€” |  | [Wikipedia](https://en.wikipedia.org/wiki/File_spanning) |
| [SQX](https://en.wikipedia.org/wiki/SQX) | `Sqx` | `.sqx` | R/W | âœ… | defrag Â· wipe |  | [encode.su](https://encode.su/threads/1290-SQX-(by-SpeedProject)) |
| [StuffIt](https://en.wikipedia.org/wiki/StuffIt) | `StuffIt` | `.sit` | R/W | âœ… | defrag Â· wipe |  | [GitHub](https://github.com/MacPaw/XADMaster) |
| [StuffIt X](https://en.wikipedia.org/wiki/StuffIt) | `StuffItX` | `.sitx` | WORM | âœ… | wipe | Writer emits the envelope shell only; the proprietary element catalog is not synthesised | [GitHub](https://github.com/MacPaw/XADMaster) |
| [Split WIM (.swm)](https://en.wikipedia.org/wiki/Windows_Imaging_Format) | `Swm` | `.swm` `.swm2` `.swm3` `.swm4` â€¦ | WORM | âœ… | wipe | XPRESS, XPRESS Huffman, LZX, LZMS, or store; sibling parts are required to extract a multi-part set | [Microsoft Learn](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/split-a-windows-image--wim--file-to-span-across-multiple-dvds) |
| [T64 (Commodore tape image)](https://en.wikipedia.org/wiki/T64_(file_format)) | `T64` | `.t64` | R/W | âœ… | defrag Â· wipe |  | [vice-emu.sourceforge.net](https://vice-emu.sourceforge.io/) |
| [TAR](https://en.wikipedia.org/wiki/Tar_(computing)) | `Tar` | `.tar` | R/W | âœ… | defrag Â· shrink Â· wipe |  | [pubs.opengroup.org](https://pubs.opengroup.org/onlinepubs/9699919799/utilities/pax.html) |
| UHARC | `Uharc` | `.uha` | R/W | âœ… | defrag Â· wipe |  | [Archive Team](http://fileformats.archiveteam.org/wiki/UHARC) |
| [WIM](https://en.wikipedia.org/wiki/Windows_Imaging_Format) | `Wim` | `.wim` `.swm` `.esd` | WORM | âœ… | wipe | LZX / XPRESS / LZMS paths; kept create-only because an append edit would break the checksum chain | [wimlib.net](https://wimlib.net/) |
| Wrapster | `Wrapster` |  | WORM | âœ… | defrag Â· wipe | MP3-wrapper archive carrying one member; stays WORM by design | [Archive Team](http://fileformats.archiveteam.org/wiki/Wrapster) |
| [XAR](https://en.wikipedia.org/wiki/Xar_(archiver)) | `Xar` | `.xar` | R/W | âœ… | defrag Â· wipe |  | [GitHub](https://github.com/mackyle/xar) |
| xDisk / GDC (Amiga) | `xDisk` | `.xdsk` `.gdc` | WORM | âœ… | defrag | Whole-disk archiver: entries are track_NNN.raw | [Aminet](https://aminet.net) |
| xMash (Amiga) | `xMash` | `.xmsh` | WORM | âœ… | defrag | Whole-disk archiver: entries are track_NNN.raw | [Aminet](https://aminet.net) |
| ZAP (Amiga) | `Zap` | `.zap` | WORM | âœ… | wipe | Whole-disk archiver: entries are track_NNN.raw | [Aminet](https://aminet.net/) |
| [ZIP](https://en.wikipedia.org/wiki/ZIP_(file_format)) | `Zip` | `.zip` `.zipx` | R/W | âœ… | defrag Â· shrink Â· wipe Â· optimize | Store, Deflate, Deflate64, Shrink, Reduce, Implode, BZip2, LZMA, PPMd, Zstd, AES | [pkware.cachefly.net](https://pkware.cachefly.net/webdocs/casestudies/APPNOTE.TXT) |
| [ZOO](https://en.wikipedia.org/wiki/Zoo_(file_format)) | `Zoo` | `.zoo` | R/W | âœ… | defrag Â· wipe |  | [Archive Team](http://fileformats.am«ëŒ+Š×®º+º$zzb¥ç&6†—fWFVÒæ÷&r÷v–¶’õ¤ôò’À§Âµ¥Ò†‡GG3¢òöVâçv–¶—VF–æ÷&r÷v–¶’õ¥’Â§Âç§Â"õrÂ)ÈRÂFVg&rÂ&VFW"6÷fW'2F†R7F÷&VB÷6–×ÆRÖöFVÇ3²¥Âf—'GVÂÖÖ6†–æRW†V7WF–öâ—2æ÷B–×ÆVÖVçFVBÂ¶ÖGFÖ†öæW’ææWEÒ†‡GG¢òöÖGFÖ†öæW’ææWBöF2÷§æ‡FÖÂ’À ¢222	úzÂ7G'V7GW&VBFFæB&Vv—7G' §Âf÷&ÖBÂ–BÂW‡FVç6–öç2Â7FFRÂFW7BÂÖ–çFVææ6RÂæ÷FW2Â&VfW&Væ6RÀ§ÂÒÒÒÂÒÒÒÂÒÒÒÂ¢ÒÒÓ¢Â¢ÒÒÓ¢ÂÒÒÒÂÒÒÒÂÒÒÒÀ§Â¥4ôâÂ§6öæÂæ§6öæÂtõ$ÒÂ)ÈRÂ(	BÂ$d2ƒ#S’ö&¦V7Bö'&’&ö¦V7F–öã²5t"Ö7&VFVB&–æ'’ÆVfW2W6RfW'6–öæVB&6ScBVçfVÆ÷Râw&—FW"–ææVB'—FRÖf÷"Ö'—FRv–ç7B5—F†öâ§6öâæGV×2†ö&¢Â–æFVçCÓ"–²&VFW"v–ç7BFö7VÖVçB5—F†öâw&÷FRÂµ$d2ƒ#S•Ò†‡GG3¢ò÷wwrç&f2ÖVF—F÷"æ÷&r÷&f2÷&f3ƒ#S’’À§Â„ÔÂÂ†ÖÆÂç†ÖÆÂtõ$ÒÂ)ÈRÂ(	BÂ„ÔÂã&ö¦V7F–öâv—F‚EDBöW‡FW&æÂÖVçF—G’&W6öÇWF–öâF—6&ÆVC²5t"7&VF–öâW6W2æÖW76VB&6†—fRVçfVÆ÷Râ&VFW"–ææVBv–ç7BFö7VÖVçB—F†öâw2VÆVÖVçEG&VRw&÷FS²F†Rw&—FW"†2æò'—FR×&—G’vFRÂ&V6W6RæòGvò„ÔÂw&—FW'2w&VRöâFV6Æ&F–öâV÷F–æræBæÖW76RÆ6VÖVçBÂæB—2–ç7FVB6†V6¶VBÆ—fRv–ç7B&VÂ'6W"–âF†RGf—6÷'’F–W"Âµs42„ÔÂãÒ†‡GG3¢ò÷wwrçs2æ÷&rõE"÷†ÖÂò’À§ÂÖW76vU6²ÂÖW76vU6¶Âæ×6w6¶æ×¶Âtõ$ÒÂ)ÈRÂ(	BÂÖ2Â'&—2Â66Æ'2æBW‡FVç6–öâ–ÆöG3²ÖævVB&VFW"÷w&—FW"âw&—FW"–ææVB'—FRÖf÷"Ö'—FRv–ç7B—F†öâÖ×6w6²6¶&²&VFW"v–ç7BfV7F÷"6÷fW&–æræ–Âö&ööÂÂF†Rv†öÆR–çFVvW"v–GF‚ÆFFW"ÂfÆöC3"æBfÆöCcBÂF†R7G"×g2Ö&–â7Æ—BÂæW7F–ærÂæBW‡BG—W2–æ6ÇVF–ærF–ÖW7F×Â´ÖW76vU6²7V6–f–6F–öåÒ†‡GG3¢òöv—F‡V"æ6öÒö×6w6²ö×6w6²ö&Æö"öÖ7FW"÷7V2æÖB’À§Â—F†öâ–6¶ÆRÂ–6¶ÆVÂç¶Æç–6¶ÆVÂtõ$ÒÂ)ÈRÂ(	BÂæöâÖW†V7WF–ær÷6öFRdÓ²æWfW"–×÷'G2ÖöGVÆW2÷"–çfö¶W2tÄô$Æò$TET4Vò%T”ÄF6ÆÆ&ÆW2â&VG2&÷Fö6öÇ2ÓRÂV6‚–ææVBv–ç7B÷WGWB5—F†öâw&÷FS²w&—FW2&÷Fö6öÂBÂ–ææVB'—FRÖf÷"Ö'—FRv–ç7B–6¶ÆRæGV×2†ö&¢Â&÷Fö6öÃÓB–Âµ—F†öåÒ†‡GG3¢òöFö72ç—F†öâæ÷&ró2öÆ–'&'’÷–6¶ÆRæ‡FÖÂ’À§ÂW&Â7F÷&&ÆRÂ7F÷&&ÆVÂç7F÷&&ÆVç7FöÂtõ$ÒÂ)ÈRÂ(	BÂ6fR÷'F&ÆRöæWGv÷&²Ö÷&FW"7V'6WC²W†V7WF&ÆRÂ&ÆW76VBæBF–VBf÷&×2&R&V¦V7FVBâ&÷F‚F—&V7F–öç2–ææVBv–ç7B&VÂW&Âç7F÷&V÷WGWBÂµW&Â7F÷&&ÆUÒ†‡GG3¢ò÷W&ÆFö2çW&Âæ÷&rõ7F÷&&ÆR’À§ÂÕ2Ôå$$bò&–æ'”f÷&ÖGFW"Âç&&fÂæç&&fÂ"Â)ÈRÂ(	BÂæöâÖ–ç7FçF–F–ærÕ2Ôå$$b&VFW#²æò76VÖ&Ç’ÆöF–ærÂ6öç7G'V7F÷'2÷"6ÆÆ&6·2â&VBÖöæÇ“¢æ÷F†–ær†W&Rw&—FW2å$$bÂæBF†R&VFW"—2–ææVBv–ç7B7G&V×2ääUB&–æ'”f÷&ÖGFW&w&÷FRÂ´Õ2Ôå$$eÒ†‡GG3¢òöÆV&âæÖ–7&÷6ögBæ6öÒöVâ×W2ö÷Vç7V72÷v–æF÷w5÷&÷Fö6öÇ2ö×2Öç&&bò’À§Âv–æF÷w2&Vv—7G'’W‡÷'BÂ&VvÂç&VvÂtõ$ÒÂ)ÈRÂ(	BÂöffÆ–æR$TtTD•CFò&Vv—7G'’VF—F÷"FW‡B–çFW&6†ævS²æWfW"F÷V6†W2F†RÆ—fR&Vv—7G'’âw&—FW"–ææVB'—FRÖf÷"Ö'—FRv–ç7B&VræW†RW‡÷'FÂ$ôÒÂ5$ÄbæBƒÖ6öÇVÖâ†W‚6öçF–çVF–öâ–æ6ÇVFVC²&VFW"v–ç7BâW‡÷'B6÷fW&–ær$Tuõ5¦Â$TuôU…äEõ5¦Â$TuôÕTÅD•õ5¦Â$TuôEtõ$FÂ$Tuõtõ$FæBw&VB$Tuô$”ä%–Â´Ö–7&÷6ögEÒ†‡GG3¢òöÆV&âæÖ–7&÷6ögBæ6öÒöVâ×W2÷v–æF÷w2×6W'fW"öFÖ–æ—7G&F–öâ÷v–æF÷w2Ö6öÖÖæG2÷&VrÖW‡÷'B’À§Âv–æF÷w2—‚&Vv—7G'’†—fR„5$Tr’Â7&VvÂæFFæFöçöÆÂ"Â)ÈRÂ(	BÂv–æF÷w2“Ró“‚ôÖR&–æ'’†—fW3²&VBÖöæÇ’Â&V6W6RæòF†—&B'G’w&—FW2öæRf÷"w&—FW"Fò&R6†V6¶VBv–ç7Bâ&VFW"vFVBöâ&VÂU4U"äDF¢ÆÂ#3RVçG&–W2Â¶W’æÖW2&W6öÇfVB'’$tD"&V6÷&B–FVçF–f–W"Â†fffb&VB2&æò¶W’ÖæÖRVçG'’"Â¶Æ–&7&Vrf÷&ÖBæ÷FW5Ò†‡GG3¢òöv—F‡V"æ6öÒöÆ–'–ÂöÆ–&7&Vrö&Æö"öÖ–âöFö7VÖVçFF–öâõv–æF÷w2S#—‚S#&Vv—7G'’S#f–ÆRS#„5$Tr’S#f÷&ÖBæ66––Fö2’À§Âv–æF÷w2åBÖfÖ–Ç’&Vv—7G'’†—fR…$Ttb’Â&VvfÂæ†—fæ†—fVæ‡fVÂ"Â)ÈRÂ(	BÂ$Ttbã(	3ãbÂ–æ6ÇVF–ærÆVv7’åBæBÖöFW&â7FæF&BöÆFW7BÆ–÷WG3²&VBÖöæÇ’Â&V6W6R&VræW†R6fVæVVG26T&6·W&—f–ÆVvVæBæòvFV&ÆRw&—FW"÷&6ÆRW†—7G2â&VFW"vFVBöâ&VÂåEU4U"äDF¢ÆÂ3“bVçG&–W2Â´Ö–7&÷6ögB&Vv—7G'’f–ÆW5Ò†‡GG3¢òöÆV&âæÖ–7&÷6ögBæ6öÒöVâ×W2÷v–æF÷w2÷v–ã3"÷7—6–æfò÷&Vv—7G'’Ö†—fR’À ¢222	ù:b6ögGv&R6¶vW2æB–ç7FÆÆW'0 §Âf÷&ÖBÂ–BÂW‡FVç6–öç2Â7FFRÂFW7BÂÖ–çFVææ6RÂæ÷FW2Â&VfW&Væ6RÀ§ÂÒÒÒÂÒÒÒÂÒÒÒÂ¢ÒÒÓ¢Â¢ÒÒÓ¢ÂÒÒÒÂÒÒÒÂÒÒÒÀ§Â´æG&ö–B'VæFÆRò7Æ—BµÒ†‡GG3¢òöVâçv–¶—VF–æ÷&r÷v–¶’ôæG&ö–Eôô'VæFÆR’ÂæG&ö–D'VæFÆVÂæ&æ·6Â"õrÂ)ÈRÂFVg&r+rv—RÂÂ¶FWfVÆ÷W"ææG&ö–Bæ6öÕÒ†‡GG3¢òöFWfVÆ÷W"ææG&ö–Bæ6öÒöwV–FRöÖ'VæFÆR’À§ÂæG&ö–BõD–ÆöBÂæG&ö–D÷FÂÂtõ$ÒÂ)ÈRÂ(	BÂ7&VFRVÖ—G2v†öÆRÖ–ÖvR–ÆöC²—B&RÖÆ—7G22–ÆöB&Æö'2Âæ÷Bf–ÆW2Â·6÷W&6RææG&ö–Bæ6öÕÒ†‡GG3¢ò÷6÷W&6RææG&ö–Bæ6öÒöFö72ö6÷&Rö÷F’À§Â´µÒ†‡GG3¢òöVâçv–¶—VF–æ÷&r÷v–¶’ôµò†f–ÆUöf÷&ÖB’’Â¶Âæ¶Â"õrÂ)ÈRÂFVg&r+rv—RÂÂ¶FWfVÆ÷W"ææG&ö–Bæ6öÕÒ†‡GG3¢òöFWfVÆ÷W"ææG&ö–Bæ6öÒöwV–FRö6ö×öæVçG2ögVæFÖVçFÇ2’À§Â´²æF—fRÆ–'&&–W5Ò†‡GG3¢òöVâçv–¶—VF–æ÷&r÷v–¶’ôµò†f–ÆUöf÷&ÖB’’Â´æF—fTÆ–'6ÂÂtõ$ÒÂ)ÈRÂv—RÂ6WVFòÖ&6†—fR÷fW"Æ–"óÆ&“âò¢ç6òÂ¶FWfVÆ÷W"ææG&ö–Bæ6öÕÒ†‡GG3¢òöFWfVÆ÷W"ææG&ö–Bæ6öÒöæF²öwV–FW2ö&—2’À§Â´–ÖvUÒ†‡GG3¢òöVâçv–¶—VF–æ÷&r÷v–¶’ô–ÖvR’Â–ÖvVÂä–ÖvVæ–ÖvVÂtõ$ÒÂ)ÈRÂ(	BÂTÄb7GV"ÇW2VæFVB7V6„e3²7&VF–öâFVÆVvFW2FòF†R7V6„e2w&—FW"Â´v—D‡V%Ò†‡GG3¢òöv—F‡V"æ6öÒô–ÖvRô–ÖvU7V2’À§Â´…Ò†‡GG3¢òöVâçv–¶—VF–æ÷&r÷v–¶’ô‚’Â†Âæ†æ×6—†Â"õrÂ)ÈRÂFVg&r+rv—RÂÂ´Ö–7&÷6ögBÆV&åÒ†‡GG3¢òöÆV&âæÖ–7&÷6ögBæ6öÒöVâ×W2÷v–æF÷w2ö×6—‚ò’À§Â´æG&ö–B&W6÷W&6W2æ'65Ò†‡GG3¢òöVâçv–¶—VF–æ÷&r÷v–¶’ôµò†f–ÆUöf÷&ÖB’’Â'66Âæ'66Â"Â)ÈRÂ)¶¬{®0®+^zºè¬è‘ééŠ—€” |  | [android.googlesource.com](https://android.googlesource.com/platform/frameworks/base/+/master/libs/androidfw/include/androidfw/ResourceTypes.h) |
| Electron asar | `Asar` | `.asar` | WORM | âœ… | â€” | JSON header plus concatenated payload | [GitHub](https://github.com/electron/asar) |
| BitRock InstallBuilder | `BitRock` |  | R | âœ… | â€” | Metakit VFS with LZMA payloads | [installbuilder.com](https://installbuilder.com) |
| [Rust crate](https://en.wikipedia.org/wiki/Cargo_(software)) | `Crate` | `.crate` | WORM | âœ… | â€” | tar.gz with the crate directory layout | [doc.rust-lang.org](https://doc.rust-lang.org/cargo/reference/registries.html#publish) |
| [CRX](https://en.wikipedia.org/wiki/Google_Chrome#Extensions) | `Crx` | `.crx` | WORM | âœ… | defrag Â· wipe | CRX3 envelope creation is unsigned and not browser-trusted | [chromium.googlesource.com](https://chromium.googlesource.com/chromium/src/+/main/components/crx_file/) |
| [Debian .deb](https://en.wikipedia.org/wiki/Deb_(file_format)) | `Deb` | `.deb` | R/W | âœ… | defrag Â· wipe |  | [debian.org](https://www.debian.org/doc/debian-policy/) |
| [EAR](https://en.wikipedia.org/wiki/EAR_(file_format)) | `Ear` | `.ear` | R/W | âœ… | defrag Â· wipe |  | [jakarta.ee](https://jakarta.ee/specifications/platform/) |
| [Ruby gem](https://en.wikipedia.org/wiki/RubyGems) | `Gem` | `.gem` | WORM | âœ… | â€” | TAR with gzip-compressed metadata and data members | [docs.ruby-lang.org](https://docs.ruby-lang.org/en/3.0/Gem/Format.html) |
| [Inno Setup](https://en.wikipedia.org/wiki/Inno_Setup) | `InnoSetup` |  | WORM | âœ… | â€” | Extraction plus signature/container output, not an installer compiler; some versions expose no per-file extraction | [sourceforge.net](https://sourceforge.net/projects/innounp/) |
| [IPA](https://en.wikipedia.org/wiki/.ipa) | `Ipa` | `.ipa` | R/W | âœ… | defrag Â· wipe |  | [pkware.cachefly.net](https://pkware.cachefly.net/webdocs/casestudies/APPNOTE.TXT) |
| [JAR](https://en.wikipedia.org/wiki/JAR_(file_format)) | `Jar` | `.jar` | R/W | âœ… | defrag Â· wipe |  | [docs.oracle.com](https://docs.oracle.com/en-us/javase/8/docs/technotes/guides/jar/jar.html) |
| [MSI](https://en.wikipedia.org/wiki/Windows_Installer) | `Msi` | `.msi` `.msp` `.mst` | R/W | âœ… | wipe | CFB envelope; a functional Installer database is not synthesised | [Microsoft Learn](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-cfb/) |
| [MSIX](https://en.wikipedia.org/wiki/MSIX) | `Msix` | `.msix` `.msixbundle` | R/W | âœ… | defrag Â· wipe | Unsigned fresh package output | [Microsoft Learn](https://learn.microsoft.com/en-us/windows/msix/) |
| [NSIS](https://en.wikipedia.org/wiki/Nullsoft_Scriptable_Install_System) | `Nsis` |  | WORM | âœ… | defrag | Extraction plus overlay-oriented output, not an installer compiler; some versions expose no per-file extraction | [nsis.sourceforge.io](https://nsis.sourceforge.io/Docs/) |
| [NuGet .nupkg](https://en.wikipedia.org/wiki/NuGet) | `NuPkg` | `.nupkg` | R/W | âœ… | defrag Â· wipe |  | [Microsoft Learn](https://learn.microsoft.com/nuget/reference/nuspec) |
| [OVA](https://en.wikipedia.org/wiki/Open_Virtualization_Format) | `Ova` | `.ova` | WORM | âœ… | â€” | Stays WORM: the manifest must cover every member | [dmtf.org](https://www.dmtf.org/standards/ovf) |
| [Pack200](https://en.wikipedia.org/wiki/Pack200) | `Pack200` | `.pack` | R | âœ… | â€” |  | [docs.oracle.com](https://docs.oracle.com/javase/8/docs/technotes/guides/pack200/pack-spec.html) |
| [PyInstaller onefile](https://en.wikipedia.org/wiki/PyInstaller) | `PyInstaller` |  | R | âœ… | â€” | CArchive TOC plus PYZ modules; Linux builds are detected as ELF first | [GitHub](https://github.com/pyinstaller/pyinstaller) |
| [RPM](https://en.wikipedia.org/wiki/RPM_Package_Manager) | `Rpm` | `.rpm` | WORM | âœ… | defrag Â· wipe |  | [GitHub](https://github.com/rpm-software-management/rpm) |
| [Snap](https://en.wikipedia.org/wiki/Snap_(software)) | `Snap` | `.snap` | WORM | âœ… | â€” | SquashFS package | [snapcraft.io](https://snapcraft.io/docs) |
| [WAR](https://en.wikipedia.org/wiki/WAR_(file_format)) | `War` | `.war` | R/W | âœ… | defrag Â· wipe |  | [jakarta.ee](https://jakarta.ee/specifications/servlet/) |
| [Python wheel](https://en.wikipedia.org/wiki/Wheel_(software)) | `Wheel` | `.whl` | WORM | âœ… | wipe | ZIP plus dist-info | [peps.python.org](https://peps.python.org/pep-0427/) |
| [XPI](https://en.wikipedia.org/wiki/XPInstall) | `Xpi` | `.xpi` | R/W | âœ… | defrag Â· wipe |  | [extensionworkshop.com](https://extensionworkshop.com/) |

### ğŸ“„ Documents, e-books, mail and web bundles

| Format | Id | Extensions | State | Test | Maintenance | Notes | Reference |
| --- | --- | --- | :---: | :---: | --- | --- | --- |
| [Adobe Illustrator](https://en.wikipedia.org/wiki/Adobe_Illustrator_Artwork) | `Ai` |  | R | âœ… | â€” |  | [Archive Team](http://fileformats.archiveteam.org/wiki/Adobe_Illustrator) |
| [DOC](https://en.wikipedia.org/wiki/Doc_(computing)) | `Doc` | `.doc` | R/W | âœ… | wipe | CFB envelope; Word document streams are not synthesised | [Microsoft Learn](https://learn.microsoft.com/en-us/openspecs/office_file_formats/ms-doc/) |
| [DOCX](https://en.wikipedia.org/wiki/Office_Open_XML) | `Docx` | `.docx` | R/W | âœ… | defrag Â· wipe |  | [ecma-international.org](https://ecma-international.org/publications-and-standards/standards/ecma-376/) |
| [EML](https://en.wikipedia.org/wiki/Email#Message_format) | `Eml` | `.eml` | R/W | âœ… | â€” |  | [RFC](https://www.rfc-editor.org/rfc/rfc5322) |
| [EPUB](https://en.wikipedia.org/wiki/EPUB) | `Epub` | `.epub` | R/W | âœ… | defrag Â· wipe |  | [w3.org](https://www.w3.org/TR/epub-33/) |
| [FB2](https://en.wikipedia.org/wiki/FictionBook) | `Fb2` | `.fb2` | R | âœ… | â€” |  | [GitHub](https://github.com/gribuser/fb2) |
| [FLA](https://en.wikipedia.org/wiki/Adobe_Animate) | `Fla` |  | R | âœ… | wipe | ZIP-based XFL document | [Microsoft Learn](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-cfb/) |
| [KMZ](https://en.wikipedia.org/wiki/Keyhole_Markup_Language) | `Kmz` | `.kmz` | R/W | âœ… | defrag Â· wipe |  | [developers.google.com](https://developers.google.com/kml/documentation) |
| [LIT (Microsoft Reader)](https://en.wikipedia.org/wiki/Microsoft_Reader) | `Lit` | `.lit` | R | âœ… | â€” |  | [Archive Team](http://fileformats.archiveteam.org/wiki/Microsoft_Reader) |
| [MAFF](https://en.wikipedia.org/wiki/Mozilla_Archive_Format) | `Maff` | `.maff` | R/W | âœ… | defrag Â· wipe |  | [maf.mozdev.org](http://maf.mozdev.org/maff-specification.html) |
| [mbox (Unix mailbox)](https://en.wikipedia.org/wiki/Mbox) | `Mbox` | `.mbox` `.mbx` | R/W | âœ… | â€” | Entries are message_NN.eml | [RFC](https://www.rfc-editor.org/rfc/rfc4155) |
| [MOBI / AZW](https://en.wikipedia.org/wiki/Mobipocket) | `Mobi` | `.mobi` `.prc` `.azw` `.azw3` | WORM | âœ… | â€” | Creates MOBI 7 books from one UTF-8 HTML document (stored or PalmDOC), accepted by KindleUnpack; reads stored and PalmDOC text with trailing entries; HUFF/CDIC and DRM text stay raw records | [wiki.mobileread.com](https://wiki.mobileread.com/wiki/MOBI) |
| [MSG](https://en.wikipedia.org/wiki/MSG_(file_format)) | `Msg` | `.msg` | R/W | âœ… | wipe | CFB envelope; MAPI properties are not synthesised | [Microsoft Learn](https://learn.microsoft.com/en-us/openspecs/exchange_server_protocols/ms-oxmsg/) |
| [ODP](https://en.wikipedia.org/wiki/OpenDocument) | `Odp` | `.odp` | R/W | âœ… | defrag Â· wipe |  | [libreoffice.org](https://www.libreoffice.org) |
| [ODS](https://en.wikipedia.org/wiki/OpenDocument) | `Ods` | `.ods` | R/W | âœ… | defrag Â· wipe |  | [libreoffice.org](https://www.libreoffice.org) |
| [ODT](https://en.wikipedia.org/wiki/OpenDocument) | `Odt` | `.odt` | R/W | âœ… | defrag Â· wipe |  | [libreoffice.org](https://www.libreoffice.org) |
| [Microsoft OneNote](https://en.wikipedia.org/wiki/Microsoft_OneNote) | `OneNote` | `.one` `.onetoc2` | R | âœ… | â€” |  | [Microsoft Learn](https://learn.microsoft.com/en-us/openspecs/office_file_formats/ms-onestore/) |
| [PDF](https://en.wikipedia.org/wiki/PDF) | `Pdf` | `.pdf` | R/W | âœ… | wipe | Image extraction and file-attachment surface, not a page renderer or editor | [ISO](https://www.iso.org/standard/75839.html) |
| [PPT](https://en.wikipedia.org/wiki/Microsoft_PowerPoint) | `Ppt` | `.ppt` | R/W | âœ… | wipe | CFB envelope; presentation streams are not synthesised | [Microsoft Learn](https://learn.microsoft.com/en-us/openspecs/office_file_formats/ms-ppt/6be79dde-33c1-4c1b-8ccc-4b2301c08662) |
| [PPTX](https://en.wikipedia.org/wiki/Office_Open_XML) | `Pptx` | `.pptx` | R/W | âœ… | defrag Â· wipe |  | [ecma-international.org](https://ecma-international.org/publications-and-standards/standards/ecma-376/) |
| [PST / OST](https://en.wikipedia.org/wiki/Personal_Storage_Table) | `Pst` | `.pst` `.ost` | R | âœ… | â€” |  | [Microsoft Learn](https://learn.microsoft.com/en-us/openspecs/office_file_formats/ms-pst/141923d5-15ab-4ef1-a524-6dce75aae546) |
| [Sketch](https://en.wikipedia.org/wiki/Sketch) | `Sketch` |  | R | âœ… | wipe |  | [developer.sketch.com](https://developer.sketch.com/file-format/) |
| [Thumbs.db](https://en.wikipedia.org/wiki/Windows_thumbnail_cache) | `ThumbsDb` | `.db` | R/W | âœ… | wipe | CFB envelope; catalog streams are not synthesised | [Microsoft Learn](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-cfb/53989ce4-7b05-4f8d-829b-d08d6148375b) |
| [TNEF (winmail.dat)](https://en.wikipedia.org/wiki/Transport_Neutral_Encapsulation_Format) | `Tnef` | `.dat` `.tnef` | R/W | âœ… | defrag |  | [GitHub](https://github.com/Yeraze/ytnef) |
| [VSDX](https://en.wikipedia.org/wiki/Microsoft_Visio) | `Vsdx` | `.vsdx` `.vstx` `.vssx` `.vsdm` â€¦ | R/W | âœ… | defrag Â· wipe |  | [ecma-international.org](https://ecma-international.org/publications-and-standards/standards/ecma-376/) |
| WACZ | `Wacz` | `.wacz` | WORM | âœ… | wipe | ZIP around WARC plus package metadata | [specs.webrecorder.net](https://specs.webrecorder.net/wacz/1.1.1/) |
| [WARC](https://en.wikipedia.org/wiki/WARC_(file_format)) | `Warc` | `.warc` | WORM | âœ… | defrag Â· wipe | Entries are listed as "resource: name"; create emits resource records | [iipc.github.io](https://iipc.github.io/warc-specifications/) |
| Web Bundle | `Wbn` | `.wbn` | WORM | âœ… | â€” | Minimal CBOR walk; create collapses inputs into one bundle | [datatracker.ietf.org](https://datatracker.ietf.org/doc/draft-ietf-wpack-bundled-responses/) |
| [WordPerfect](https://en.wikipedia.org/wiki/WordPerfect) | `WordPerfect` | `.wpd` `.wp` `.wp5` `.wp6` â€¦ | R | âœ… | â€” |  | [sourceforge.net](https://sourceforge.net/projects/libwpd/) |
| [XLS](https://en.wikipedia.org/wiki/Microsoft_Excel#File_formats) | `Xls` | `.xls` | R/W | âœ… | wipe | CFB envelope; workbook streams are not synthesised | [Microsoft Learn](https://learn.microsoft.com/en-us/openspecs/office_file_formats/ms-xls/cd03cb5f-ca02-4934-a391-bb674cb8aa06) |
| [XLSX](https://en.wikipedia.org/wiki/Office_Open_XML) | `Xlsx` | `.xlsx` | R/W | âœ… | defrag Â· wipe |  | [ecma-international.org](https://ecma-international.org/publications-and-standards/standards/ecma-376/) |
| [XPS / OpenXPS](https://en.wikipedia.org/wiki/Open_XML_Paper_Specification) | `Xps` | `.xps` `.oxps` | R/W | âœ… | defrag Â· wipe |  | [ecma-international.org](https://ecma-international.org/publications-and-standards/standards/ecma-388/) |

### ğŸ® Game, engine and console archives

| Format | Id | Extensions | State | Test | Maintenance | Notes | Reference |
| --- | --- | --- | :---: | :---: | --- | --- | --- |
| Sega AFS | `Afs` | `.afs` | R/W | âœ… | defrag Â· wipe | Alignment and metadata block paths | [GitHub](https://github.com/MaikelChan/AFSPacker) |
| Square Enix AKB | `Akb` | `.akb` | WORM | âœ… | defrag Â· wipe | Entries are entry_NNN.bin | [GitHub](https://github.com/vgmstream/vgmstream) |
| CRI AWB / AFS2 | `Awb` | `.awb` `.acb` | WORM | âœ… | defrag Â· wipe | Entries are cue_NNNNN.bin | [GitHub](https://github.com/vgmstream/vgmstream) |
| Bethesda BA2 | `Ba2` | `.ba2` | R/W | âœ… | defrag Â· wipe | BTDX GNRL scope | [en.uesp.net](https://en.uesp.net/wiki/Skyrim_Mod:File_Formats/BA2) |
| EA / Westwood BIG | `Big` | `.big` | R/W | âœ… | defrag Â· wipe |  | [MultimediaWiki](https://wiki.multimedia.cx/index.php/Electronic_Arts_Formats) |
| Bethesda BSA | `Bsa` | `.bsa` | R/W | âœ… | defrag Â· wipe |  | [en.uesp.net](https://en.uesp.net/wiki/Skyrim_Mod:File_Formats/BSA) |
| [Bloodlines DZIP](https://en.wikipedia.org/wiki/Vampire:_The_Masquerade_%E2%80%93Bloodlines) | `Dzip` | `.dzip` | R/W | âœ… | defrag Â· wipe |  | â€” |
| [GameMaker data.win](https://en.wikipedia.org/wiki/GameMaker) | `GameMaker` | `.win` `.unx` `.ios` | WORM | âœ… | â€” | Entries are chunks/<TAG>.bin | [GitHub](https://github.com/UnderminersTeam/UndertaleModTool) |
| Nintendo 3DS GAR | `Gar` | `.gar` | R/W | âœ… | defrag Â· wipe |  | [3dbrew.org](https://www.3dbrew.org/wiki/GAR) |
| [Game Boy ROM](https://en.wikipedia.org/wiki/Game_Boy) | `Gb` | `.gb` `.gbc` | R | âœ… | â€” |  | [gbdev.io](https://gbdev.io/pandocs/The_Cartridge_Header.html) |
| LucasArts GOB | `Gob` | `.gob` `.goo` | R/W | âœ… | defrag Â· wipe |  | [GitHub](https://github.com/luciusDXL/TheForceEngine) |
| Godot PCK | `GodotPck` | `.pck` | R/W | âœ… | defrag Â· wipe |  | [docs.godotengine.org](https://docs.godotengine.org/en/stable/contributing/development/file_formats/pck.html) |
| Build engine GRP | `Grp` | `.grp` | R/W | âœ… | defrag Â· wipe |  | [moddingwiki.shikadi.net](https://moddingwiki.shikadi.net/wiki/GRP_Format) |
| Descent HOG | `Hog` | `.hog` | R/W | âœ… | defrag Â· wipe |  | [GitHub](https://github.com/dxx-rebirth/dxx-rebirth) |
| [Total Annihilation HPI](https://en.wikipedia.org/wiki/Total_Annihilation) | `Hpi` | `.hpi` `.ufo` `.ccx` `.gp3` | R/W | âœ… | defrag Â· wipe | Unencrypted / zlib subset | [units.tauniverse.com](https://units.tauniverse.com/tutorials/tadesign/tutorials/hpi.htm) |
| LucasArts LFD | `Lfd` | `.lfd` | WORM | âœ… | defrag Â· wipe | Entries are DATA.<stem> and RMAP.resource | [GitHub](https://github.com/MikeG621/LfdReader) |
| Minecraft region (MCA) | `Mca` | `.mca` `.mcr` | R | âœ… | â€” |  | [minecraft.wiki](https://minecraft.wiki/w/Region_file_format) |
| Cyan Mohawk | `Mhk` | `.mhk` | WORM | âœ… | defrag Â· wipe | Entries are typed tDAT_NNNN names | [GitHub](https://github.com/scummvm/scummvm) |
| Westwood MIX | `Mix` | `.mix` | WORM | âœ… | defrag Â· wipe | Hash-keyed names; hex names are synthesised where the original is absent | [GitHub](https://github.com/OpenRA/OpenRA) |
| [Blizzard MPQ](https://en.wikipedia.org/wiki/MPQ) | `Mpq` | `.mpq` | R/W | âœ… | defrag Â· wipe |  | [zezula.net](http://www.zezula.net/en/mpq/main.html) |
| Nintendo NARC | `Narc` | `.narc` `.carc` | R/W | âœ… | defrag Â· wipe |  | [problemkaputt.de](https://problemkaputt.de/gbatek.htm) |
| [Nintendo DS ROM](https://en.wikipedia.org/wiki/Nintendo_DS) | `Nds` | `.nds` | R/W | âœ… | defrag Â· wipe | NitroFS-oriented output, not ARM boot-code synthesis | [problemkaputt.de](https://problemkaputt.de/gbatek.htm) |
| [NES ROM](https://en.wikipedia.org/wiki/INES) | `Nes` | `.nes` | R | âœ… | â€” |  | [nesdev.org](https://www.nesdev.org/wiki/INES) |
| NScripter NSA | `Nsa` | `.nsa` | R/W | âœ… | defrag Â· wipe |  | [nscripter.com](https://www.nscripter.com/) |
| [Quake PAK](https://en.wikipedia.org/wiki/PAK_(file_format)) | `Pak` | `.pak` | R/W | âœ… | defrag Â· wipe |  | [GitHub](https://github.com/id-Software/Quake) |
| PSP PBP | `Pbp` | `.pbp` | WORM | âœ… | defrag Â· wipe | Fixed EBOOT section names only | [psdevwiki.com](https://www.psdevwiki.com/psp/PBP) |
| Nintendo Switch PFS0 / NSP | `Pfs0` | `.nsp` `.pfs0` | R/W | âœ… | defrag Â· wipe |  | [switchbrew.org](https://switchbrew.org/wiki/NCA#PFS0) |
| Sony PSARC | `Psarc` | `.psarc` | R/W | âœ… | defrag Â· wipe | zlib block path; encrypted and LZMA variants are rejected; names stored lower-case | [psdevwiki.com](https://www.psdevwiki.com/ps3/PlayStation_archive_(PSARC)) |
| [Portable Sound Format](https://en.wikipedia.org/wiki/Portable_Sound_Format) | `Psf` | `.psf` `.psf2` `.minipsf` `.minipsf2` â€¦ | WORM | âœ… | â€” |  | [web.archive.org](https://web.archive.org/web/20060212232218/http://wiki.neillcorlett.com/PSFFormat) |
| Nintendo RARC | `Rarc` | `.arc` `.rarc` | WORM | âœ… | defrag Â· wipe | Entries are typed tDAT_NNNN names | [wiki.cloudmodding.com](https://wiki.cloudmodding.com/zgcn/ARC) |
| RPG Maker RGSSAD | `Rgss` | `.rgssad` `.rgss2a` `.rgss3a` | R/W | âœ… | defrag Â· wipe |  | [GitHub](https://github.com/morkt/GARbro) |
| Ren'Py RPA | `Rpa` | `.rpa` | R/W | âœ… | defrag Â· wipe |  | [GitHub](https://github.com/renpy/renpy) |
| NScripter SAR | `Sar` | `.sar` | R/W | âœ… | defrag Â· wipe | Uncompressed NSA family | [nscripter.com](https://www.nscripter.com/) |
| Nintendo SARC | `Sarc` | `.sarc` `.pack` `.bars` | R/W | âœ… | defrag Â· wipe | Endian-aware reader; hash-sorted writer | [zeldamods.org](https://zeldamods.org/wiki/SARC) |
| BioWare SFAR | `Sfar` | `.sfar` | WORM | âœ… | wipe | LZX-compressed payload extraction is limited | [GitHub](https://github.com/ME3Tweaks/LegendaryExplorer) |
| Sir-Tech SLF | `Slf` | `.slf` | R/W | âœ… | defrag Â· wipe |  | [GitHub](https://github.com/ja2-stracciatella/ja2-stracciatella) |
| [SNES ROM](https://en.wikipedia.org/wiki/Super_Nintendo_Entertainment_System) | `Snes` | `.sfc` `.smc` `.fig` `.swc` | R | âœ… | â€” |  | [snes.nesdev.org](https://snes.nesdev.org/wiki/ROM_header) |
| Mass Effect TFC | `Tfc` | `.tfc` | WORM | âœ… | â€” | Entries are bundle_NNNNN.bin | [GitHub](https://github.com/ME3Tweaks/LegendaryExplorer) |
| Nintendo U8 | `U8` | `.u8` `.arc` | R/W | âœ… | defrag Â· wipe |  | [wiibrew.org](https://wiibrew.org/wiki/U8_archive) |
| Unreal UMX | `Umx` | `.umx` | WORM | âœ… | wipe | Header/package shell output only; the export table is not encoded | [wiki.beyondunreal.com](https://wiki.beyondunreal.com/Legacy:Package_File_Format) |
| Unity asset bundle | `UnityBundle` | `.bundle` `.unity3d` `.assetbundle` | R/W | âœ… | defrag Â· optimize | BlocksInfo-at-end bundles edit in place by appending tail blocks; other layouts rebuild | [docs.unity3d.com](https://docs.unity3d.com/Manual/AssetBundlesIntro.html) |
| Unreal .pak | `UnrealPak` | `.pak` | WORM | âœ… | defrag |  | [GitHub](https://github.com/panzi/u4pak) |
| Valve VPK | `Vpk` | `.vpk` | R/W | âœ… | defrag Â· wipe |  | [developer.valvesoftware.com](https://developer.valvesoftware.com/wiki/VPK) |
| Volition VPP v1 | `Vpp` | `.vpp` | R/W | âœ… | defrag Â· wipe |  | [GitHub](https://github.com/gibbed/Gibbed.Volition) |
| Volition VPP v2 | `VppV2` | `.vpp_pc` | R/W | âœ… | defrag Â· wipe |  | [GitHub](https://github.com/gibbed/Gibbed.Volition) |
| [Doom WAD](https://en.wikipedia.org/wiki/Doom_WAD) | `Wad` | `.wad` | R/W | âœ… | defrag Â· wipe | Lump names are 8 characters | [doomwiki.org](https://doomwiki.org/wiki/WAD) |
| Quake / Half-Life WAD2/WAD3 | `Wad2` | `.wad` | R/W | âœ… | defrag Â· wipe |  | [developer.valvesoftware.com](https://developer.valvesoftware.com/wiki/WAD) |
| YukaScript YPF | `Ypf` | `.ypf` | R/W | âœ… | defrag Â· wipe |  | [GitHub](https://github.com/morkt/GARbro) |
| [ZX Spectrum snapshot / tape](https://en.wikipedia.org/wiki/ZX_Spectrum_software) | `ZxSnapshot` | `.sna` `.z80` `.tap` `.tzx` | R | âœ… | â€” |  | [sinclair.wiki.zxnet.co.uk](https://sinclair.wiki.zxnet.co.uk/wiki/TAP_format) |

### ğŸ’¾ Backup and disk-image containers

| Format | Id | Extensions | State | Test | Maintenance | Notes | Reference |
| --- | --- | --- | :---: | :---: | --- | --- | --- |
| [Acronis True Image .tib](https://en.wikipedia.org/wiki/Acronis_True_Image) | `AcronisTib` | `.tib` | R/W | â€” | â€” | FileMeta chain and InputItem attribute streams decoded from reverse-engineered evidence | [GitHub](https://github.com/dennisss/acronis-tib) |
| [Acronis .tibx](https://en.wikipedia.org/wiki/Acronis_Tibx) | `AcronisTibx` | `.tibx` | R | âœ… | â€” | Page-frame walk plus LSM sub-header; record-stream decode is bounded | [acronis.com](https://www.acronis.com) |
| [AFF4](https://en.wikipedia.org/wiki/Advanced_Forensic_Format) | `Aff4` |  | R | âœ… | â€” |  | [GitHub](https://github.com/aff4/Standard) |
| AOMEI Backupper .adi/.afi | `Aomei` | `.adi` `.afi` | R/W | âœ… | â€” | BIFH/BIFT and BR header/index structures; no vendor byte-compat claim for own output | [aomeitech.com](https://www.aomeitech.com) |
| [Microsoft NTBackup (MTF)](https://en.wikipedia.org/wiki/NTBackup) | `Bkf` | `.bkf` | R/W | âœ… | â€” |  | [Archive Team](http://fileformats.archiveteam.org/wiki/Microsoft_Tape_Format) |
| EaseUS Todo Backup .pbd | `EaseUsPbd` | `.pbd` | R | âœ… | â€” | Chunk-stream extraction path | [easeus.com](https://www.easeus.com) |
| [Symantec / Norton Ghost](https://en.wikipedia.org/wiki/Ghost_(disk_utility)) | `Ghost` | `.gho` `.ghs` | R/W | âœ… | â€” |  | [Archive Team](http://fileformats.archiveteam.org/wiki/Ghost_image) |
| Macrium Reflect X | `Macrium` | `.mrimgx` `.mrbakx` `.mrimg` | WORM | âœ… | â€” | Open-spec .mrimgx path; entries are disk-image.raw plus block-NN.$* members | [GitHub](https://github.com/macrium/mrimgx_file_layout) |
| Macrium Reflect pre-X | `MacriumPreX` | `.mrimg` `.mrbak` `.mrex` `.mrsql` | R | âœ… | â€” |  | [macrium.com](https://www.macrium.com) |
| Paragon .pbf | `Paragon` | `.pbf` | R/W | âœ… | â€” | Own clean-room container path; entries are chunk_NNNNNN.bin, so edits address chunks | [paragon-software.com](https://www.paragon-software.com) |
| [partclone (Clonezilla)](https://en.wikipedia.org/wiki/Clonezilla) | `Partclone` | `.aa` `.img` | R | âœ… | â€” |  | [partclone.org](https://partclone.org) |
| [Apple Sparsebundle](https://en.wikipedia.org/wiki/Sparse_image) | `Sparsebundle` | `.sparsebundle` | R | âœ… | â€” |  | [developer.apple.com](https://developer.apple.com/library/archive/documentation/Darwin/Reference/ManPages/man1/hdiutil.1.html) |
| [Apple Sparseimage](https://en.wikipedia.org/wiki/Sparse_image) | `Sparseimage` | `.sparseimage` | R/W | âœ… | â€” |  | [developer.apple.com](https://developer.apple.com/library/archive/documentation/Darwin/Reference/ManPages/man1/hdiutil.1.html) |
| [Veeam .vbk/.vib/.vrb](https://en.wikipedia.org/wiki/Veeam) | `Veeam` | `.vbk` `.vib` `.vrb` | R | âœ… | â€” | Summary/trailer path only; the undocumented block layer is not guessed | [GitHub](https://github.com/synacktiv/veeam-velociraptor) |
| VMware VIB | `Vib` | `.vib` | WORM | âœ… | â€” |  | [blogs.vmware.com](https://blogs.vmware.com/cloud-foundation/2011/09/13/whats-in-a-vib/) |

### ğŸ§© Executables, resources and other pseudo-archives

| Format | Id | Extensions | State | Test | Maintenance | Notes | Reference |
| --- | --- | --- | :---: | :---: | --- | --- | --- |
| [PE resources (.rsrc)](https://en.wikipedia.org/wiki/Portable_Executable) | `PeResources` | `.dll` `.exe` `.ocx` `.cpl` â€¦ | R | âœ… | â€” |  | [Microsoft Learn](https://learn.microsoft.com/windows/win32/debug/pe-format) |
| [Resource-only DLL](https://en.wikipedia.org/wiki/Dynamic_link_library) | `ResourceDll` |  | WORM | âœ… | â€” |  | [Microsoft Learn](https://learn.microsoft.com/windows/win32/debug/pe-format) |
| [ELF](https://en.wikipedia.org/wiki/Executable_and_Linkable_Format) | `Elf` | `.elf` `.so` `.o` `.ko` | R | âœ… | â€” |  | [sco.com](https://www.sco.com/developers/gabi/) |
| [Mach-O](https://en.wikipedia.org/wiki/Mach-O) | `MachO` | `.macho` `.dylib` `.bundle` `.o` | R | âœ… | â€” |  | [GitHub](https://github.com/apple-oss-distributions/xnu) |
| [DOS MZ executable](https://en.wikipedia.org/wiki/DOS_MZ_executable) | `Mz` | `.exe` `.com` `.ovl` `.bin` | R | âœ… | â€” |  | [Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/debug/pe-format) |
| [.NET assembly](https://en.wikipedia.org/wiki/.NET_assembly) | `NetAssembly` |  | R | âœ… | â€” |  | [ecma-international.org](https://ecma-international.org/publications-and-standards/standards/ecma-335/) |
| [WebAssembly module](https://en.wikipedia.org/wiki/WebAssembly) | `Wasm` | `.wasm` | R | âœ… | â€” |  | [webassembly.github.io](https://webassembly.github.io/spec/core/binary/index.html) |
| [Windows ICO/CUR](https://en.wikipedia.org/wiki/ICO_(file_format)) | `Ico` | `.ico` | R/W | âœ… | defrag |  | [Microsoft Learn](https://learn.microsoft.com/en-us/previous-versions/ms997538(v=msdn.10)) |
| [Windows CUR cursor](https://en.wikipedia.org/wiki/ICO_(file_format)) | `Cur` | `.cur` | WORM | âœ… | defrag |  | [Microsoft Learn](https://learn.microsoft.com/en-us/previous-versions/ms997538(v=msdn.10)) |
| [ANI (animated cursor)](https://en.wikipedia.org/wiki/ANI_(file_format)) | `Ani` | `.ani` | WORM | âœ… | â€” |  | [Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/menurc/about-cursors) |
| [TTC](https://en.wikipedia.org/wiki/TrueType#TrueType_Collection) | `Ttc` | `.ttc` | WORM | âœ… | â€” | Inputs must be .ttf/.otf fonts | [Microsoft Learn](https://learn.microsoft.com/en-us/typography/opentype/spec/) |
| [OTC](https://en.wikipedia.org/wiki/OpenType) | `Otc` | `.otc` | R | âœ… | â€” |  | [Microsoft Learn](https://learn.microsoft.com/en-us/typography/opentype/spec/) |
| [TTF (per-glyph)](https://en.wikipedia.org/wiki/TrueType) | `Ttf` | `.ttf` | R | âœ… | â€” |  | [Microsoft Learn](https://learn.microsoft.com/en-us/typography/opentype/spec/) |
| [OTF (per-glyph)](https://en.wikipedia.org/wiki/OpenType) | `Otf` | `.otf` | R | âœ… | â€” |  | [Microsoft Learn](https://learn.microsoft.com/en-us/typography/opentype/spec/) |
| [gettext .mo](https://en.wikipedia.org/wiki/Gettext) | `Mo` | `.mo` | WORM | âœ… | â€” | Entries are NNNN_<stem>.txt | [gnu.org](https://www.gnu.org/software/gettext/manual/html_node/MO-Files.html) |
| [gettext .po](https://en.wikipedia.org/wiki/Gettext) | `Po` | `.po` `.pot` | R | âœ… | â€” |  | [gnu.org](https://www.gnu.org/software/gettext/manual/html_node/PO-Files.html) |
| [AppleSingle](https://en.wikipedia.org/wiki/AppleSingle_and_AppleDouble_formats) | `AppleSingle` | `.as` `.applesingle` | R/W | âœ… | â€” | Inputs must map to AppleSingle entry ids | [RFC](https://www.rfc-editor.org/rfc/rfc1740) |
| [AppleDouble](https://en.wikipedia.org/wiki/AppleSingle_and_AppleDouble_formats) | `AppleDouble` | `.appledouble` | R/W | âœ… | â€” | Same body as AppleSingle under the sidecar magic; a data fork is refused, it belongs in the sibling file | [RFC](https://www.rfc-editor.org/rfc/rfc1740) |
| [PKCS #12](https://en.wikipedia.org/wiki/PKCS_12) | `Pkcs12` | `.p12` `.pfx` | R | âœ… | â€” |  | [RFC](https://www.rfc-editor.org/rfc/rfc7292) |
| [PAR2](https://en.wikipedia.org/wiki/Parchive) | `Par2` | `.par2` | R | âœ… | â€” |  | [parchive.sourceforge.net](https://parchive.sourceforge.net) |
| [Motorola S-record](https://en.wikipedia.org/wiki/SREC_(file_format)) | `Srec` | `.s19` `.s28` `.s37` `.srec` â€¦ | WORM | âœ… | â€” | Entries are metadata.ini plus firmware.bin | [srecord.sourceforge.net](https://srecord.sourceforge.net) |
| [Windows shell link](https://en.wikipedia.org/wiki/Shortcut_(computing)) | `Lnk` | `.lnk` | WORM | âœ… | â€” | Entries are header.bin / linkinfo.bin | [Microsoft Learn](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-shllink/) |
| [PCAP](https://en.wikipedia.org/wiki/Pcap) | `Pcap` | `.pcap` `.cap` | R | âœ… | â€” |  | [tcpdump.org](https://www.tcpdump.org) |
| [PCAPNG](https://en.wikipedia.org/wiki/Pcap) | `Pcapng` | `.pcapng` `.ntar` | R | âœ… | â€” |  | [GitHub](https://github.com/pcapng/pcapng) |

### ğŸ§ª Scientific, data and CAD containers

| Format | Id | Extensions | State | Test | Maintenance | Notes | Reference |
| --- | --- | --- | :---: | :---: | --- | --- | --- |
| [Apache Arrow IPC](https://en.wikipedia.org/wiki/Apache_Arrow) | `Arrow` | `.arrow` `.feather` | R | âœ… | â€” |  | [arrow.apache.org](https://arrow.apache.org/docs/format/Columnar.html) |
| [Apache Avro OCF](https://en.wikipedia.org/wiki/Apache_Avro) | `Avro` | `.avro` | R | âœ… | â€” |  | [avro.apache.org](https://avro.apache.org/docs/current/specification/) |
| [Collada (.dae)](https://en.wikipedia.org/wiki/COLLADA) | `Collada` | `.dae` | R | âœ… | â€” |  | [collada.org](http://www.collada.org/2005/11/COLLADASchema) |
| [DICOM](https://en.wikipedia.org/wiki/DICOM) | `Dicom` | `.dcm` `.dicom` | R | âœ… | â€” |  | [dicom.nema.org](https://dicom.nema.org/medical/dicom/current/output/html/part10.html) |
| [DICOMDIR](https://en.wikipedia.org/wiki/DICOM) | `DicomDir` | `.dcmdir` | R | âœ… | â€” |  | [dicom.nema.org](https://dicom.nema.org/medical/dicom/current/output/chtml/part10/chapter_8.html) |
| [DXF (AutoCAD Drawing Exchange)](https://en.wikipedia.org/wiki/AutoCAD_DXF) | `Dxf` | `.dxf` | R | âœ… | â€” |  | [help.autodesk.com](https://help.autodesk.com/view/OARX/2022/ENU/?guid=GUID-235B22E0-A567-4CF6-92D3-38A2306D73F3) |
| [FITS](https://en.wikipedia.org/wiki/FITS) | `Fits` | `.fits` `.fit` `.fts` | WORM | âœ… | wipe | Entries are hdu_* header/data members | [fits.gsfc.nasa.gov](https://fits.gsfc.nasa.gov) |
| [HDF4](https://en.wikipedia.org/wiki/Hierarchical_Data_Format) | `Hdf4` | `.hdf` `.hdf4` `.h4` | R | âœ… | â€” |  | [hdfgroup.org](https://www.hdfgroup.org/solutions/hdf4/) |
| [HDF5](https://en.wikipedia.org/wiki/Hierarchical_Data_Format) | `Hdf5` | `.h5` `.hdf5` | R | âœ… | â€” |  | [GitHub](https://github.com/HDFGroup/hdf5) |
| [Apache Iceberg metadata](https://en.wikipedia.org/wiki/Apache_Iceberg) | `Iceberg` |  | R | âœ… | â€” |  | [iceberg.apache.org](https://iceberg.apache.org/spec/) |
| [LevelDB SSTable](https://en.wikipedia.org/wiki/LevelDB) | `Leveldb` | `.ldb` `.sst` | R | âœ… | â€” |  | [GitHub](https://github.com/google/leveldb) |
| [MATLAB MAT v5](https://en.wikipedia.org/wiki/MATLAB) | `Matlab` | `.mat` | R | âœ… | â€” |  | [mathworks.com](https://www.mathworks.com/help/pdf_doc/matlab/matfile_format.pdf) |
| [MATLAB MAT v4](https://en.wikipedia.org/wiki/MATLAB) | `MatlabV4` | `.mat` | R | âœ… | â€” |  | [mathworks.com](https://www.mathworks.com/help/pdf_doc/matlab/matfile_format.pdf) |
| [Access MDB / ACCDB](https://en.wikipedia.org/wiki/Microsoft_Access) | `Mdb` | `.mdb` `.accdb` | R | âœ… | â€” |  | [GitHub](https://github.com/mdbtools/mdbtools) |
| [NetCDF (Classic)](https://en.wikipedia.org/wiki/NetCDF) | `NetCdf` | `.nc` `.cdf` | R | âœ… | â€” |  | [unidata.ucar.edu](https://www.unidata.ucar.edu/software/netcdf/) |
| [NIfTI](https://en.wikipedia.org/wiki/Neuroimaging_Informatics_Technology_Initiative) | `Nifti` | `.nii` | R | âœ… | â€” |  | [nifti.nimh.nih.gov](https://nifti.nimh.nih.gov/) |
| [NumPy .npy](https://en.wikipedia.org/wiki/NumPy) | `Npy` | `.npy` | WORM | âœ… | â€” | Single array: header.bin plus array.bin | [numpy.org](https://numpy.org/doc/stable/reference/generated/numpy.lib.format.html) |
| [NumPy .npz](https://en.wikipedia.org/wiki/NumPy) | `Npz` | `.npz` | WORM | âœ… | wipe | Members carry the .npy suffix | [numpy.org](https://numpy.org/doc/stable/reference/generated/numpy.lib.format.html) |
| [Wavefront OBJ (3D model)](https://en.wikipedia.org/wiki/Wavefront_.obj_file) | `Obj` | `.obj` | R | âœ… | â€” |  | [paulbourke.net](https://paulbourke.net/dataformats/obj/) |
| [ONNX](https://en.wikipedia.org/wiki/Open_Neural_Network_Exchange) | `Onnx` | `.onnx` | R | âœ… | â€” |  | [GitHub](https://github.com/onnx/onnx/blob/main/onnx/onnx.proto) |
| [Apache ORC](https://en.wikipedia.org/wiki/Apache_ORC) | `Orc` | `.orc` | R | âœ… | â€” |  | [orc.apache.org](https://orc.apache.org/specification/) |
| [Apache Parquet](https://en.wikipedia.org/wiki/Apache_Parquet) | `Parquet` | `.parquet` | R | âœ… | â€” |  | [GitHub](https://github.com/apache/parquet-format) |
| [PLY (Stanford polygon)](https://en.wikipedia.org/wiki/PLY_(file_format)) | `Ply` | `.ply` | R | âœ… | â€” |  | [paulbourke.net](http://paulbourke.net/dataformats/ply/) |
| [SQLite 3 Database](https://en.wikipedia.org/wiki/SQLite) | `Sqlite` | `.sqlite` `.sqlite3` `.db3` | R | âœ… | â€” |  | [sqlite.org](https://www.sqlite.org/fileformat2.html) |
| [STL (stereolithography)](https://en.wikipedia.org/wiki/STL_(file_format)) | `Stl` | `.stl` | R | âœ… | â€” |  | [fabbers.com](https://www.fabbers.com/tech/STL_Format) |
| [Autodesk 3DS](https://en.wikipedia.org/wiki/.3ds) | `Tds` | `.3ds` | R | âœ… | â€” |  | [paulbourke.net](http://paulbourke.net/dataformats/3ds/) |
| [TFRecord](https://en.wikipedia.org/wiki/TensorFlow) | `TfRecord` | `.tfrecord` `.tfrecords` | WORM | âœ… | defrag Â· wipe | Entries are record_NNNNN.bin | [tensorflow.org](https://www.tensorflow.org/tutorials/load_data/tfrecord) |
| [Zarr array metadata](https://en.wikipedia.org/wiki/Zarr_(data_format)) | `Zarr` |  | R | âœ… | â€” |  | [zarr-specs.readthedocs.io](https://zarr-specs.readthedocs.io/) |

### ğŸï¸ Media containers

| Container | Id | Extensions | Demux | Mux | Remux / edit | Notes | Reference |
| --- | --- | --- | :---: | :---: | :---: | --- | --- |
| [ASF / WMV / WMA](https://en.wikipedia.org/wiki/Advanced_Systems_Format) | `Asf` | `.asf` `.wma` `.wmv` | âœ… | âœ… | âœ… | Header Object children and Data Object walked; unencrypted audio streams muxed and remuxed from the canonical stream entries | [Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/wmformat/overview-of-the-asf-format) |
| [AVI](https://en.wikipedia.org/wiki/Audio_Video_Interleave) | `Avi` | `.avi` | âœ… | âœ… | âœ… | movi demux; AVI 1.0 mux/remux preserves movi packet order and idx1 flags; elementary mux accepts video frames plus PCM audio; header chunks can be relocated in place | [Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/directshow/avi-riff-file-reference) |
| [Bink](https://en.wikipedia.org/wiki/Bink_Video) | `Bik` | `.bik` `.bk2` | âœ… | âœ… | â€” | Reverse-engineered; packet-aware demux preserves frame sizes, keyframes and interleaving for encoded-stream mux/remux; no codec encoder | [MultimediaWiki](https://wiki.multimedia.cx/index.php/Bink_Container) |
| [FLV](https://en.wikipedia.org/wiki/Flash_Video) | `Flv` | `.flv` | âœ… | âœ… | âœ… | AVC re-framed as Annex-B, AAC as ADTS, MP3 raw; other codecs as concatenated frames. Mux takes AAC/MP3 audio packets only; remux rewrites the container preserving native tag payloads, timestamps and order | [rtmp.veriskope.com](https://rtmp.veriskope.com/pdf/video_file_format_spec_v10_1.pdf) |
| [HLS M3U8](https://en.wikipedia.org/wiki/HTTP_Live_Streaming) | `M3u8` | `.m3u8` `.m3u` | âœ… | â€” | â€” | A manifest, not a container: it references segments it does not hold, so there is nothing to mux | [RFC](https://www.rfc-editor.org/rfc/rfc8216) |
| [Matroska / WebM](https://en.wikipedia.org/wiki/Matroska) | `Mkv` | `.mkv` `.webm` `.mka` `.mks` | âœ… | âœ… | âœ… | Tracks, attachments and chapters; Cues can be moved to the front in place | [matroska.org](https://www.matroska.org/technical/elements.html) |
| [MP4 / MOV / 3GP](https://en.wikipedia.org/wiki/MP4_file_format) | `Mp4` | `.mp4` `.m4v` `.m4a` `.mov` â€¦ | âœ… | âœ… | âœ… | Track demux; audio-only mux from AAC/PCM inputs; fast-start relayout in place | [ISO](https://www.iso.org/standard/83102.html) |
| [MPEG program stream / VOB](https://en.wikipedia.org/wiki/MPEG_program_stream) | `MpegPs` | `.mpg` `.mpeg` `.vob` `.m2p` â€¦ | âœ… | âœ… | âœ… | PES headers stripped; DVD private-stream-1 substreams (AC-3, DTS, LPCM, sub-picture) split. Mux rebuilds an MPEG-2 program stream from MPEG-1/2, MPEG-4 Part 2, AVC, HEVC, MPEG-audio and AAC elementary streams; DVD private streams stay read-only | [ISO](https://www.iso.org/standard/75928.html) |
| [MPEG transport stream](https://en.wikipedia.org/wiki/MPEG_transport_stream) | `MpegTs` | `.ts` `.m2ts` `.mts` | âœ… | âœ… | â€” | Per-PID elementary streams as raw PES; mux writes one PAT/PMT and packetises the supplied elementary streams | [ISO](https://www.iso.org/standard/75928.html) |
| [RealMedia](https://en.wikipedia.org/wiki/RealMedia) | `RealMedia` | `.rm` `.rmvb` `.ra` | âœ… | âœ… | â€” | Reverse-engineered; demux plus encoded-audio mux and remux, no video writer | [MultimediaWiki](https://wiki.multimedia.cx/index.php/RealMedia) |
| [Smacker](https://en.wikipedia.org/wiki/Smacker_video) | `Smk` | `.smk` | âœ… | â€” | â€” | Reverse-engineered; no writer | [MultimediaWiki](https://wiki.multimedia.cx/index.php/Smacker) |
| [Blu-ray PGS (.sup)](https://en.wikipedia.org/wiki/Presentation_Graphic_Stream) | `Sup` | `.sup` | âœ… | âœ… | âœ… | Complete PCS-to-END display sets can be reassembled losslessly; bitmap/timing authoring is outside this pseudo-archive surface | [GitHub](https://github.com/mjuhasz/BDSup2Sub) |
| [VobSub](https://en.wikipedia.org/wiki/VobSub) | `VobSub` | `.idx` | âœ… | âœ… | â€” | Index plus one sub-picture stream, not a multi-track container; raw `.spu` input muxes to 2 KiB MPEG-PS sectors and extracted `.bin` chunks are written back byte-exact | [sam.zoy.org](http://sam.zoy.org/writings/dvd/subtitles/) |

### ğŸ›¡ï¸ Executable packers (descriptors)

| Packer | Id | Extensions | State | Notes | Reference |
| --- | --- | --- | :---: | --- | --- |
| [UPX](https://en.wikipedia.org/wiki/UPX) | `Upx` |  | R | Signature/evidence detection plus in-process NRV payload decompression | [GitHub](https://github.com/upx/upx) |
| [ASPack](https://en.wikipedia.org/wiki/ASPack) | `AsPack` |  | R |  | [aspack.com](http://www.aspack.com) |
| ASProtect | `AsProtect` |  | R |  | [aspack.com](http://www.aspack.com) |
| bzexe | `Bzexe` |  | R |  | [sourceware.org](https://sourceware.org/bzip2/) |
| Crinkler | `Crinkler` |  | R |  | [GitHub](https://github.com/runestubbe/Crinkler) |
| FSG | `Fsg` |  | R |  | [GitHub](https://github.com/horsicq/Detect-It-Easy) |
| GoPacker | `GoPacker` |  | R |  | [GitHub](https://github.com/packing-box/docker-packing-box) |
| [gzexe](https://en.wikipedia.org/wiki/Gzip) | `Gzexe` |  | R |  | [gnu.org](https://www.gnu.org/software/gzip/manual/gzip.html) |
| Huan | `Huan` |  | R |  | [GitHub](https://github.com/frkngksl/Huan) |
| kkrunchy | `Kkrunchy` |  | R |  | [GitHub](https://github.com/farbrausch/fr_public) |
| [LZEXE (DOS exe)](https://en.wikipedia.org/wiki/LZEXE) | `LzExe` |  | R |  | [Archive Team](http://fileformats.archiveteam.org/wiki/LZEXE) |
| MEW | `Mew` |  | R |  | [GitHub](https://github.com/horsicq/Detect-It-Easy) |
| MPRESS | `MPress` |  | R |  | [matcode.com](https://matcode.com/) |
| NsPack | `NsPack` |  | R |  | [GitHub](https://github.com/horsicq/Detect-It-Easy) |
| Origami | `Origami` |  | R |  | [GitHub](https://github.com/dr4k0nia/Origami) |
| Papaw | `Papaw` |  | R |  | [GitHub](https://github.com/dimkr/papaw) |
| PEtite | `Petite` |  | R |  | [un4seen.com](https://www.un4seen.com/petite/) |
| [PKLITE (DOS exe)](https://en.wikipedia.org/wiki/PKLITE) | `PkLite` |  | R |  | [Archive Team](http://fileformats.archiveteam.org/wiki/PKLITE) |
| Shrinkler | `Shrinkler` |  | R |  | [GitHub](https://github.com/askeksa/Shrinkler) |
| Silent_Packer | `SilentPacker` |  | R |  | [GitHub](https://github.com/SilentVoid13/Silent_Packer) |
| [Themida](https://en.wikipedia.org/wiki/Themida) | `Themida` |  | R |  | [oreans.com](https://www.oreans.com/themida.php) |
| [VMProtect](https://en.wikipedia.org/wiki/VMProtect) | `VmProtect` |  | R |  | [vmpsoft.com](https://vmpsoft.com) |
| Yoda's Crypter | `YodaCrypter` |  | R |  | [sourceforge.net](https://sourceforge.net/projects/yodap/) |

### ğŸ› ï¸ Executable packer handlers

`FileFormat.ExePackers` and `FileFormat.Upx` carry the packer descriptors above plus the `IExecutablePackerHandler` implementations that detect a packer, locate its payload and, where the format is understood, inflate it with the package's own building blocks. Levels: **Unpack** â€” payload located and decompressed to a memory image (a byte-identical pre-packing file is generally unreachable because packers rebuild imports, relocations and resources); **Locate** â€” packer recognised and its payload emitted, decompression not yet wired; **Detect** â€” recognition and diagnostics only (runtime protectors).

| Packer | Level | Core / notes |
| --- | --- | --- |
| UPX | Unpack | NRV2B/D/E and LZMA cores; full detect â†’ decompress â†’ memory image â†’ synthetic rebuild. LZMA-mode payloads (method 14) are located and reported, not decoded. |
| ASPack | Unpack | Own LZ77 + Huffman core (`AsPackLzDecoder`), not aPLib. Region table drives an in-place restore of every packed section; the E8/E9 call filter is reversed. |
| BeRoEXEPacker | Unpack | Entry stub parsed for its immediates; LZMA (129 of 130 samples) or aPLib body decoded, E8/E9 filter reversed, `reconstructed.exe` emitted. |
| Eronana Packer | Unpack | Static LZ77 + canonical-Huffman decoder validated byte-for-byte against a real sample. |
| Enigma Virtual Box | Unpack* | `.enigma1`/`.enigma2` recognised; sampled corpus inflates through the managed aPLib path. Bundled file-tree extraction remains. |
| MEW | Unpack* | Section layout recognised; managed generic payload recovery emits `reconstructed.exe`; other variants fall back to payload location. |
| Molebox | Unpack | 2.x loader chain replayed: LCG keystream over an LZSS'd loader blob, IDEA-protected configuration, per-section IDEA + zlib. All 415 recoverable sections in the corpus come back byte-identical. |
| MPRESS | Unpack | 2.x: bare LZMA1 stream behind MPRESS's own 8-byte header, decoded through `BB_Lzma`; E8/E9 transform reversed. 1.x packs with another codec and stays at payload location. |
| Packman | Unpack | Shared aPLib PE pipeline; decompressed payload plus synthetic rebuilt PE. |
| PEtite | Unpack | Block table behind the entry stub replayed; every block inflated with the PEtite DEFLATE dialect; absolute-branch transform reversed. Imports, relocations and OEP are not rebuilt. |
| RLPack | Unpack | Own `{sourceRva, destinationRva}` block table, one bare LZMA or aPLib stream per section; x86 call/jump filter reversed. All 130 corpus samples decompress. |
| WinUpack | Unpack | Upack's LZMA-idiom range coder plus call/jump filter, driven by the loader's parameter block; both container shapes decode. |
| Yoda's Crypter | Unpack | Stub walker replays the per-build byte cipher and restores the original entry point; 129 of 130 corpus samples decrypt. |
| GZEXE / BZEXE | Unpack | Shell wrappers with embedded gzip / bzip2 payload; the original executable is restored statically. |
| Papaw | Unpack | ELF wrapper with obfuscated XZ/LZMA2 payload; appended original restored. |
| GoPacker | Unpack | Appended Zstandard executable payload restored. |
| Origami | Unpack | .NET wrapper with XORed raw-Deflate managed payload; original assembly restored. |
| PyPePacker | Unpack | Python zipapp PE wrapper; EntropyEncoding v2, RC6-CBC and gzip reversed. |
| PE-Toy | Unpack | `.petoy` shell section with aPLib payload through the shared aPLib PE pipeline. |
| Silent_Packer | Unpack | ELF64 XOR section-insertion wrapper; `.text` and entry point restored for the supported variant. |
| Huan | Unpack | PE64 loader with encrypted `.huan` section; embedded PE decrypted. |
| hXOR-Packer | Unpack | Stored and single-byte-XOR transforms reversed byte-for-byte; the bespoke-Huffman modes stay at payload location. |
| Xor_Packer | Unpack | .NET wrapper with Base64/XOR/Base64 settings; embedded PE decoded. |
| Alternate EXE Packer | Unpack | A UPX 3.96 front end; routed through the UPX pipeline rather than a duplicate detector. |
| _(generic aPLib PE)_ | Unpack | `aplib_pe` fallback: any PE whose section inflates to a clean aPLib stream. |
| _(generic NRV PE)_ | Unpack | `nrv_pe` fallback: any PE whose section inflates as NRV2B/2D/2E to a plausible payload. |
| FSG | Locate* | `FSG!` marker and t/ta/a layouts recognised; synthetic aPLib-FSG fixtures unpack through the generic path. |
| PECompact | Locate | Payload found and framed; the region is a series of compressed blocks (block sizes at offset 6), not one stream, so no codec is guessed. |
| NsPack | Locate | `nsp0`/`nsp1` layout; `nsp1` opens with the relocated resource directory, then a second-stage loader, then the compressed data â€” the table has to be read before a decoder is written. |
| Neolite | Locate* | Ordinary section names; `.text` opens with the loader and no section is dense enough to be a single compressed image. |
| JDPack / Exe32pack / eXpressor / Alienyze | Locate | Packer section emitted as `compressed_payload.bin`; custom LZ recovery remains. |
| Amber | Locate | Reflective PE loader; a plaintext embedded PE is carved when present, the XOR/RC4-obscured payload is located otherwise. |
| SimpleDpack | Locate | `.dpack` blob plus stripped-section targets emitted; the published release did not match its documented LZMA container, so nothing is decoded against an unconfirmed format. |
| PE-Packer (czs108) | Locate | `.shell` section emitted; the +0xCC cipher and import rewrite are documented but their byte ranges live in a MASM-compiled shell with no reference binary. |
| squishy | Locate | `logicoma` section and credit text recognised against real 0.1.3/0.2.0 output; closed-source context-mixing payload is not decoded. |
| Themida / WinLicense, TELock, Yoda's Protector | Detect / Locate | Runtime protectors: the protected body is emitted as `protected_section_*.bin`; no decompression is claimed. Yoda's Protector's cipher and LZO1X stream are understood, the section-name restore is not. |
| Crinkler, kkrunchy, Shrinkler | Detect | Demoscene compressing linkers with undocumented context-mixing payloads; metadata and diagnostics only. |

Measured against the [chesvectain/PackingData](https://github.com/chesvectain/PackingData) corpus (130 samples per packer): recognition 2455 of 2470; of the 1562 samples with a pre-packing original, 1300 come back with a distinctive 32-byte run of that original in the recovered body. Per-packer counts and the analysis of the still-blocked packers are in [`docs/EXE-PACKER-NOTES.md`](https://github.com/Hawkynt/CompressionWorkbench/blob/main/docs/EXE-PACKER-NOTES.md). The Packing Box manifest audit (`DatasetProbe.PackingBoxPackersManifest_IsFetchableAndAuditsRegisteredHandlers`) reports which of its 104 packer entries have no handler yet.

### ğŸ”— Compound formats

`tar.gz`, `tar.bz2`, `tar.xz`, `tar.zst`, `tar.lz4`, `tar.lz` and `tar.br` are composed from the TAR descriptor and the matching stream descriptor (`CanCompoundWithTar`). Detection and writing reuse those two layers; there is no second TAR implementation.

### ğŸš§ Gaps

- **Media containers.** AVI, MPEG-TS, RealMedia and Smacker demux only; MP4, Matroska, ASF, Bink, FLV and MPEG-PS mux audio tracks only. This is a limit of what demuxing preserves, not of the container specs. The demuxers hand back each track as a codec elementary stream â€” AVC re-framed as Annex-B, AAC as ADTS, per-PID PES â€” and per-frame timestamps, interleaving order and the index live in the container layer that is dropped on the way out. Muxing those entries back would mean inventing presentation timing rather than restoring it, so the writers are not there. Closing this needs a demux surface that carries timed packets, not another writer. Bink, Smacker, RealMedia and ASF are additionally reverse-engineered rather than specified. M3U8, PGS `.sup` and VobSub are not container muxes at all: a playlist is a manifest referencing segments it does not contain, and the two subtitle formats are single streams. ISO-BMFF brands are parsed generically, without a brand registry.
- **Whole-image and typed-input writers** (Amiga disk archivers, DMS, sparse images, PBP, ICO/CUR/ANI, TTC, AppleSingle/AppleDouble, Wrapster, OVA) create only what their format can hold; arbitrary file trees are refused with a message rather than mangled.
- **Reverse-engineered backup formats** (Acronis, AOMEI, EaseUS, Macrium, Paragon, Veeam) are decoded to the depth the evidence supports; unknown encrypted or index layers stay unknown.
- **Executable packers** blocked at Locate are listed above with the reason; the manifest audit names the unmapped ones.

## ğŸš€ Quick start

### Detect and list an archive

```csharp
using Compression.Registry;

using var input = File.OpenRead("payload.tar.gz");
var archive = FormatRegistry.DetectArchiveOperations(input);
foreach (var entry in archive.List(input))
  Console.WriteLine($"{entry.Name,-40} {entry.Size,12:N0}");
```
### Round-trip a compression stream

```csharp
using FileFormat.Brotli;

byte[] original = File.ReadAllBytes("page.html");
var format = new BrotliFormatDescriptor();
byte[] compressed = format.Compress(original);
byte[] restored = format.Decompress(compressed);
```

### Demux a media container

```csharp
using FileFormat.MpegPs;

using var vob = File.OpenRead("VTS_01_1.VOB");
var demuxer = new MpegPsFormatDescriptor();
demuxer.Extract(vob, "out", password: null, files: null); // stream_E0_mpeg2video.m2v, stream_BD_80_ac3.ac3, â€¦
```

## ğŸ—ï¸ Architecture

### Archive state model

A descriptor advertises what it can do twice, and the two must agree: a `FormatCapabilities` bit for quick gating, and the interface that carries the method the orchestrator calls.

| Implementâ€¦ | â€¦and the format gains |
| --- | --- |
| `IArchiveFormatOperations` | List / Extract / Test â€” **R** |
| `IArchiveCreatable` | Create â€” **WORM**; override `CreateFromStreams` for OOM-free creation |
| `IArchiveModifiable` | Add / Replace / Remove and purge (remove all) â€” **R/W**. The default implementation is the verified extract â†’ edit â†’ re-create rebuild; formats with a cheaper native editor override it |
| `IArchiveDefragmentable` | defrag |
| `IArchiveShrinkable` | shrink |
| `IWipeEmpty` / `IArchiveLayoutMap` | wipe (zero proven-dead gaps; the layout map also feeds the block-map preview) |
| `ILayoutOptimizable` | optimize |
| `IFileInternalLayoutMap` / `IFileInternalChunkMover` | reorder container metadata in place |
| `IStreamFormatOperations` | single-stream compress / decompress with `FormatCreateOptions` tunables |

`CanModify` is withheld from create-only formats whose checksum chain an append would break (WIM, split WIM) and from writers that reject an arbitrary edited member set (Wrapster, OVA), even though the rebuild machinery could run; `WriteCapabilityHonestyTests` enforces that every `CanModify` claimant implements `IArchiveModifiable`, and `ArchiveModifyRoundTripTests` proves the edit round-trips.

The full model â€” tiers, archive vs. pseudo-archive, the five maintenance verbs and the composite `compact`, the block-map display contract and the streaming paths â€” is specified in [`docs/ARCHIVE-MODEL.md`](https://github.com/Hawkynt/CompressionWorkbench/blob/main/docs/ARCHIVE-MODEL.md). How the verbs are provided without bespoke per-format code, and the rule that decides when `CanModify` may be advertised, are in [`docs/MAINTENANCE-MECHANISMS.md`](https://github.com/Hawkynt/CompressionWorkbench/blob/main/docs/MAINTENANCE-MECHANISMS.md). Per-verb coverage of the filesystem descriptors is the support matrix of [`Hawkynt.FileFormats.FileSystems/README.md`](https://github.com/Hawkynt/CompressionWorkbench/blob/main/Hawkynt.FileFormats.FileSystems/README.md); for the archive descriptors it is the Maintenance column above.

### On-disk derivations

Two codecs this package writes have no published specification. What was measured to make them interoperate is written up so it is not lost: [`docs/LZMS-ON-DISK.md`](https://github.com/Hawkynt/CompressionWorkbench/blob/main/docs/LZMS-ON-DISK.md) (WIM LZMS resources, verified by `wimlib-imagex verify`) and [`docs/QUANTUM-ON-DISK.md`](https://github.com/Hawkynt/CompressionWorkbench/blob/main/docs/QUANTUM-ON-DISK.md) (CAB Quantum folders, verified by `cabextract`). The BitRock installer layout is documented beside its reader in [`Hawkynt.FileFormats.Archives/FileFormats/FileFormat.BitRock/FORMAT-NOTES.md`](https://github.com/Hawkynt/CompressionWorkbench/blob/main/Hawkynt.FileFormats.Archives/FileFormats/FileFormat.BitRock/FORMAT-NOTES.md). Size ceilings of the underlying building blocks are measured in [`docs/LARGE-INPUTS.md`](https://github.com/Hawkynt/CompressionWorkbench/blob/main/docs/LARGE-INPUTS.md).

## ğŸ§­ When to use this package

Use it when a .NET process needs to enumerate, extract, test, create, edit or inspect a broad range of archives, packages, images and streams without native archive libraries. If all you need is ordinary ZIP at default settings, `System.IO.Compression.ZipArchive` is simpler. Original-vendor encoder parity for proprietary formats is not implied: readable, writable, interoperable output is the goal.

## ğŸ“š API reference

<!-- API:BEGIN generated by Hawkynt/RepositoryTemplate/package-readme â€” edit the XML docs in source, not here -->

Every public and protected member of all 2558 types, generated from the built assembly and its XML documentation, is in [REFERENCE.md](https://github.com/Hawkynt/CompressionWorkbench/blob/main/Hawkynt.FileFormats.Archives/REFERENCE.md).

<!-- API:END -->

## ğŸ”Œ Dependencies

| Dependency | Role |
| --- | --- |
| [`Hawkynt.Compression.Core`](https://www.nuget.org/packages/Hawkynt.Compression.Core/) | Shared compression, entropy, transform, bit-I/O and registry primitives |
| Native archive/compression libraries | **None required at runtime.** |

## âš ï¸ Limitations

- WORM is not R/W: creating a valid archive is different from safely editing one, and only descriptors with a proven edit path advertise `CanModify`.
- R/W by rebuild rewrites the container; formats whose listing renames entries (track images, chunked backup images, hash-keyed game archives) can only address entries by the names they list.
- LZFSE: uncompressed and LZVN blocks only. ZPAQ: no ZPAQL virtual machine. StuffIt X and UMX writers emit the envelope shell only. SFAR: LZX payload extraction is limited. Inno Setup: some versions expose no per-file extraction.
- OLE2 (DOC / XLS / PPT / MSG / Thumbs.db / MSI) creation produces a valid CFB envelope, not the application's document or database streams.
- RAR and 7z creation target the implemented RAR4/RAR5 and 7z paths, not every historical writer version, and no vendor encoder heuristic is reproduced.
- Media containers are demuxed at the container level; carried codecs are decoded only where the audio package provides them.
- MPEG-TS elementary streams are emitted as raw PES; MPEG-PS strips PES headers. PyInstaller onefile builds for Linux are detected as ELF by the stronger magic.
- Installer and package parsing is inspection only; no install logic or script is executed.
- Reverse-engineered proprietary structures are documented only to the depth evidenced by code, tests and reference binaries. Unknown structure is not filled with guesses.

## â¤ï¸ Support
If this project saves you time or money, consider supporting its development:

[![GitHub Sponsors](https://img.shields.io/badge/GitHub-Sponsors-EA4AAA?logo=githubsponsors)](https://github.com/sponsors/Hawkynt)
[![PayPal](https://img.shields.io/badge/PayPal-Donate-00457C?logo=paypal)](https://www.paypal.me/hawkynt)

## ğŸ“œ License

Licensed under LGPL-3.0-or-later â€” see the repository [LICENSE](https://github.com/Hawkynt/CompressionWorkbench/blob/main/LICENSE).
