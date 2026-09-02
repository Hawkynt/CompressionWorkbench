# Media container support

This ledger tracks containers, wrappers, raw stream envelopes, playlists, and ISO Base Media File Format brands separately from audio/video codecs. Container support means CompressionWorkbench can identify and structurally interpret the envelope; it does not imply that every carried codec can be decoded.

The authoritative runtime inventory remains `FormatRegistry` (`cwb formats`). This page is the gap/audit view used to compare that runtime inventory with historical compatibility catalogues.

## Coverage sources

- [GSpot v2.70a file types](https://www.headbands.com/gspot/filetypes.html) — historical content-based identification baseline.
- [GSpot v2.70a](https://gspot.headbands.com/) — AVI, MPEG, VOB, MP4/MOV, FLV, RealMedia, WMV/ASF and related raw-stream coverage.
- [ftyps.com](https://www.ftyps.com/) — historical MP4/QuickTime `ftyp` catalogue.
- [MP4 Registration Authority](https://mp4ra.org/) — preferred current authority for ISO-BMFF registrations.

`ftyp` values are container brands, not codec identifiers. A brand such as `avc1` does not prove that a particular track contains AVC; the track/sample entry must be inspected independently.

## States

| Mark | Meaning |
| --- | --- |
| ✅ | Detection and structural parsing/demux exist. |
| 🟨 | Useful subset exists; see notes. |
| 👁 | Identification only. |
| ⬜ | No matching implementation located in the audit. |

## Container matrix

| Container / envelope | Detect | Parse / demux | Create | Edit | Current implementation / gap |
| --- | :---: | :---: | :---: | :---: | --- |
| AVI / RIFF AVI | ✅ | ✅ | 🟨 | ⬜ | `FileFormat.Avi`; classic RIFF AVI writer exists, OpenDML is separate. |
| MP4 / MOV / ISO-BMFF | ✅ | ✅ | ⬜ | 🟨 | `FileFormat.Mp4`; track demux plus fast-start/layout surgery. |
| 3GP / 3G2 | ✅ | 🟨 | ⬜ | ⬜ | Accepted by MP4 descriptor; brand/profile-specific behavior needs audit. |
| Matroska / WebM | ✅ | ✅ | ⬜ | ⬜ | `FileFormat.Matroska`. |
| MPEG-2 Transport Stream | ✅ | ✅ | ⬜ | ⬜ | `FileFormat.MpegTs`; per-PID elementary streams. |
| MPEG Program Stream / VOB | ⬜ | ⬜ | ⬜ | ⬜ | High-value gap; PES/program-stream demux can reuse existing audio work. |
| ASF / WMV / WMA envelope | ⬜ | ⬜ | ⬜ | ⬜ | High-value gap because WMA v1/v2, Pro and Lossless decoders already exist. |
| FLV | ⬜ | ⬜ | ⬜ | ⬜ | Add FLV tag/timestamp parsing and demux first. |
| RealMedia / RealAudio | ✅ | ✅ | ⬜ | ⬜ | `FileFormat.RealMedia`; codec coverage is tracked separately. |
| Ogg / OGM | ✅ | ✅ | ⬜ | ⬜ | `FileFormat.Ogg`; carried codec support is separate. |
| Smacker `.smk` | ✅ | ✅ | ⬜ | ⬜ | `FileFormat.Smk`; audio partial, video still raw. |
| FLIC `.fli/.flc/.flx` | ✅ | ✅ | ⬜ | ⬜ | Existing frame/pseudo-archive path; variant audit remains. |
| Sierra VMD | ⬜ | ⬜ | ⬜ | ⬜ | GSpot compatibility gap. |
| Westwood VQA | ⬜ | ⬜ | ⬜ | ⬜ | GSpot compatibility gap. |
| Vivo `.viv` | ⬜ | ⬜ | ⬜ | ⬜ | GSpot compatibility gap. |
| Nullsoft NSV | ⬜ | ⬜ | ⬜ | ⬜ | GSpot compatibility gap. |
| Shockwave Flash `.swf` | ⬜ | ⬜ | ⬜ | ⬜ | Container/tag parsing should remain separate from embedded codecs. |
| WAV / RIFF WAVE | ✅ | ✅ | ✅ | ⬜ | Codec dispatch is tracked in the audio ledger. |
| RF64 / Wave64 | ✅ | ✅ | ✅ | ⬜ | Large-file WAVE-family envelopes. |
| AIFF | ✅ | ✅ | ✅ | ⬜ | Compressed AIFC variants require separate audit. |
| AU / SND | ✅ | ✅ | ✅ | ⬜ | Native audio envelope. |
| FLAC | ✅ | ✅ | ✅ | ⬜ | Native FLAC container/metadata surface. |
| WavPack | ✅ | ✅ | ✅ | ⬜ | Native WavPack file/container surface. |
| AMR raw file | ✅ | ✅ | ⬜ | ⬜ | `FileFormat.Amr`; codec modes tracked separately. |
| CD-ROM XA audio | ✅ | ✅ | ⬜ | ⬜ | `FileFormat.Xa` + XA ADPCM. |
| HLS M3U8 | ✅ | ✅ | ⬜ | ⬜ | `FileFormat.M3u8`; generic M3U remains a separate audit. |
| ASX / PLS / SMIL | ⬜ | ⬜ | ⬜ | ⬜ | Playlist/markup backlog. |
| DVD IFO/BUP | ⬜ | ⬜ | ⬜ | ⬜ | DVD navigation structures; VOB/PES belongs to MPEG Program Stream. |

## ISO-BMFF / QuickTime `ftyp` baseline

The ftyps catalogue is an alias/profile list, not a codec list. CompressionWorkbench should parse the exact four-byte `major_brand`, `minor_version`, and compatible brands; preserve case/trailing spaces; map known brands to a canonical profile for display; and continue parsing unknown brands when the underlying ISO-BMFF structure is valid.

| Brand group | Historical brands to recognize | Current handling |
| --- | --- | --- |
| ISO base / MP4 | `isom`, `iso2`, `mp41`, `mp42`, `mp71`, `mp21`, `avc1` | 🟨 Generic `ftyp` detection; explicit brand naming/validation missing. |
| QuickTime | `qt  `, `mqt ` | 🟨 Generic parsing; explicit profile labeling missing. |
| 3GPP | `3gp1`, `3gp2`, `3gp3`, `3gp4`, `3gp5`, `3gp6`, `3ge6`, `3ge7`, `3gg6`, `3gs7` | 🟨 Generic MP4 path; brand conformance unaudited. |
| 3GPP2 | `3g2a`, `3g2b`, `3g2c`, `KDDI` | 🟨 `.3g2` accepted; brand conformance unaudited. |
| Apple media | `M4A `, `M4B `, `M4P `, `M4V `, `M4VH`, `M4VP` | 🟨 Generic MP4 parsing; profile semantics missing. |
| Adobe F4 | `F4V `, `F4P `, `F4A `, `F4B ` | 🟨 Generic ISO-BMFF parsing; Adobe semantics missing. |
| Motion JPEG 2000 / JPEG 2000 | `mjp2`, `mj2s`, `JP2 `, `JP20`, `jpm `, `jpx ` | 🟨 Generic boxes; image/video codec support separate. |
| Nero Digital | `NDAS`, `NDSC`, `NDSH`, `NDSM`, `NDSP`, `NDSS`, `NDXC`, `NDXH`, `NDXM`, `NDXP`, `NDXS` | 🟨 Generic MP4 parsing; explicit brand descriptions missing. |
| DMB / MPEG-A | `da0a`, `da0b`, `da1a`, `da1b`, `da2a`, `da2b`, `da3a`, `da3b`, `dmb1`, `dv1a`, `dv1b`, `dv2a`, `dv2b`, `dv3a`, `dv3b` | ⬜ No profile-aware mapping. |
| DVB | `dvr1`, `dvt1` | ⬜ No brand-aware mapping. |
| Protected/profile brands | `isc2`, `odcf`, `opf2`, `opx2` | ⬜ No profile interpretation. |
| Vendor/camera | `CAEP`, `caqv`, `CDes`, `MSNV`, `pana`, `ROSS`, `sdv `, `ssc1`, `ssc2` | ⬜ Factual brand descriptions not mapped. |
| Other historical | `dmpf`, `drc1`, `mmp4`, `MPPI` | ⬜ Explicit brand mapping missing. |

The complete historical descriptions remain at [ftyps.com](https://www.ftyps.com/). New brand mappings should be checked against MP4RA/current specifications before becoming normative support claims.

## Priority gaps

1. ASF/WMV container wiring.
2. MPEG Program Stream/VOB/PES.
3. FLV.
4. Explicit ISO-BMFF brand registry using MP4RA plus the ftyps historical alias set.
5. VMD, VQA, Vivo and NSV as container-first legacy targets.
