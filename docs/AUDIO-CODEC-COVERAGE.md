# Audio codec support

This ledger tracks **audio codecs and codec identifiers** independently of the container that carries them. The package-level implementation inventory in [`Hawkynt.FileFormats.Audio/README.md`](../Hawkynt.FileFormats.Audio/README.md) remains the authoritative description of checked-in codec projects; this page maps those projects against external compatibility catalogues.

## Coverage sources

- [GSpot v2.70a audio codecs](https://gspot.headbands.com/audiocodecs.htm) — 245 historical WAVE/ACM format tags and aliases.
- [GSpot mirror](https://www.headbands.com/gspot/audiocodecs.htm) — same historical catalogue where the subdomain is unavailable.
- Standards-body specifications and registered format documentation are preferred for implementation behavior; GSpot is an alias/identification oracle.

The GSpot table must **not** become 245 decoder projects. Many entries are aliases, vendor registrations, transport wrappers, or several tags for the same codec family. We implement a family once and map every compatible tag to that canonical family.

## States

| Mark | Meaning |
| --- | --- |
| ✅ | Codec implementation exists for the claimed direction/profile. |
| 🟨 | Partial/profile-limited implementation. |
| 🔌 | Codec exists, but this historical WAV/AVI tag is not yet wired to it. |
| 👁 | Tag can be identified/name-resolved only. |
| ⬜ | Codec/tag family is a gap. |

## GSpot WAVE/ACM family mapping

| Family | GSpot-era identifiers | Codec | WAV/AVI tag routing | Notes |
| --- | --- | :---: | :---: | --- |
| PCM | `0x0001` | ✅ R/W | ✅ | Integer PCM; WAV dispatch exists. |
| IEEE float | `0x0003` | ✅ R | ✅ | WAV reader accepts 32/64-bit float and normalizes downstream representation. |
| G.711 A-law | `0x0006`, `0x0102` | ✅ R/W | 🟨 | `0x0006` wired; IBM alias should resolve to same family after verification. |
| G.711 μ-law | `0x0007` | ✅ R/W | ✅ | WAV dispatch exists. |
| Microsoft ADPCM | `0x0002` | ✅ R | ✅ | WAV dispatch exists. |
| IMA/DVI ADPCM | `0x0011`, `0x0039` and compatible vendor aliases | ✅ R | 🟨 | Standard `0x0011` wired; alias sweep pending. |
| OKI/Dialogic ADPCM | `0x0010`, `0x0017` | ✅ R/W | 🔌 | `Codec.OkiAdpcm` exists; WAV routing needs explicit tag mapping/tests. |
| Creative ADPCM | `0x0200` | ✅ R/W | 🔌 | Codec/container support exists in VOC; WAV tag routing is separate. |
| GSM 06.10 | `0x0031`, `0x0086`, `0x00A1`, `0x0155` | ✅ R | 🟨 | `0x0031` wired; vendor aliases need mapping tests. |
| TrueSpeech | `0x0022` | ✅ R | ✅ | `Codec.TrueSpeech`; WAV dispatch and tests exist. |
| G.721 / G.726 ADPCM | `0x0040`, `0x0045`, `0x0064`, `0x0085`, `0x008B`, `0x0140`, `0x4243`, `0xA105`, `0xA107` | ✅ R/W | 🔌 | `Codec.G72x`; tag/profile/rate routing still needs an alias table. |
| G.722 | `0x0065` | ✅ R/W | 🔌 | `Codec.G722`; WAV routing pending. |
| G.723.1 | `0x0059`, `0x0093`, `0x00A3`, `0x0123`, `0x1C0C`, `0xA100` | ✅ R | 🔌 | `Codec.G7231` and raw format descriptor exist; historical WAVE aliases need exact framing audit. |
| G.729 family | `0x0044`, `0x0083`, `0x008C`, `0x0133`, `0x0134`, `0xA103` | ⬜ | ⬜ | Separate G.729/G.729A implementation required. |
| MPEG-1/2 Audio | `0x0050`, `0x0055`, `0x0700` | ✅ R | 🔌 | MP3/MPEG Audio decoder exists; WAV/AVI tag dispatch needs consolidation. |
| AAC | `0x00B0`, `0x00FF`, `0x0180`, `0x0AAC`, `0x4143`, `0x706D`, `0xA106`, RealAudio AAC tags | 🟨 R | 🔌 | AAC implementation exists with profile boundaries; every alias must preserve actual profile/extradata semantics. |
| AC-3 | `0x0092`, `0x0241`, `0x2000` | 🟨 R | 🔌 | Distinguish raw AC-3 from S/PDIF encapsulation. |
| DTS | `0x0008`, `0x0190`, `0x2001` | ✅ R | 🔌 | Core DTS decode exists; wrapper/tag semantics need explicit routing. |
| ATRAC / ATRAC3 | `0x0063`, `0x0272` and Sony variants | 🟨 R | 🔌 | `Codec.Atrac1` / `Codec.Atrac3`; exact tag-to-generation mapping needs tests. |
| WMA v1 | `0x0160` | ✅ R | 🔌 | `Codec.Wma`; ASF container is still missing. |
| WMA v2 | `0x0161` | ✅ R | 🔌 | `Codec.Wma`; ASF container is still missing. |
| WMA Pro | `0x0162`, `0x0164` | ✅ R | 🟨 | `Codec.WmaPro`; S/PDIF variant must remain separate. |
| WMA Lossless | `0x0163` | ✅ R | 🔌 | `Codec.WmaLossless`. |
| RealAudio 14.4 | `0x2002` | ✅ R | 🔌 | `Codec.Ra144`; RealMedia/raw RA routing should own framing. |
| RealAudio 28.8 | `0x2003` | 🟨 | 🔌 | Audit exact current codec implementation/profile. |
| RealAudio Cook | `0x2004` | 🟨 R | 🔌 | `Codec.Cook`; RealMedia integration/profile audit. |
| RealAudio AAC/AAC+ | `0x2006`, `0x2007` | 🟨 R | 🔌 | Route to AAC family with RealAudio framing/profile rules. |
| SIPR / ACELP.NET | `0x0130`-`0x0135`, Vivo/SIREN-related tags | 🟨 R | 🔌 | `Codec.Sipr`/`Codec.Siren` cover part of this historical speech family; exact aliases need audit. |
| Vorbis ACM | `0x564C`, `0x674F`, `0x6750`, `0x6751`, `0x676F`, `0x6770`, `0x6771` | ✅ R | 🔌 | All compatible tags should resolve to one Vorbis family, with legacy ACM framing handled separately. |
| Speex ACM | `0xA109` | ✅ R | 🔌 | `Codec.Speex`; tag routing pending. |
| WavPack | `0x5756` | ✅ R/W | 🔌 | Native WavPack support exists; WAVE-tag route is separate. |
| FLAC | `0xF1AC` | ✅ R | 🔌 | Native FLAC support exists; WAVE-tag route is separate. |
| AMR-NB | `0x0136`, `0x4201`, `0x7A21`, `0x7A22` | ✅ R | 🔌 | `Codec.AmrNb` + raw AMR container; WAVE aliases need mapping. |
| AMR-WB | `0xA104` | ✅ R | 🔌 | `Codec.AmrWb`; WAVE alias needs mapping. |
| IBM CVSD / CVSD family | `0x0005` | ✅ R/W | 🔌 | `Codec.Cvsd`; confirm IBM registration parameters before aliasing. |
| MACE 3:1 / 6:1 | QuickTime identifiers rather than the main WAVE table | ✅ R | — | `Codec.Mace`; tracked because container codec routing must converge on the same family registry. |
| Musepack | container-native | ✅ R | — | `Codec.Musepack` SV7/SV8. |
| Monkey's Audio | container-native | 🟨 R/W | — | Supported levels documented in audio package. |
| TTA | container-native | ✅ R/W | — | `Codec.Tta`. |
| Shorten | container-native | ✅ R/W | — | `Codec.Shorten`. |

## GSpot long-tail gaps

The remaining GSpot tag catalogue is still useful even where no decoder exists. Rather than pretending each registration is a unique algorithm, track them by family/research bucket:

| Historical family | Representative GSpot tags | State |
| --- | --- | :---: |
| VSELP / Microsoft speech | `0x0004`, `0x0032`, `0x0066`, `0x0067`, `0x0082` | ⬜ |
| Voxware speech/music family | `0x0062`, `0x0069`-`0x007B`, `0x0081`, `0x181C` | ⬜ |
| Sonarc / PAC / proprietary music coders | `0x0021`, `0x0053`, `0x181E`, `0x1500` | ⬜ |
| APT-X / legacy studio codecs | `0x0025`, `0x0099` | ⬜ |
| Philips speech | `0x0098`, `0x0120`, `0x0121` | ⬜ |
| Qualcomm PureVoice/HalfRate | `0x0150`, `0x0151` | ⬜ |
| Intel Music Coder / Indeo Audio | `0x0401`, `0x0402` | ⬜ |
| QDesign Music | `0x0450` | ⬜ |
| On2 audio registrations | `0x0500`, `0x0501` | ⬜ |
| Olivetti / Lernout & Hauspie speech families | `0x1000`-`0x1004`, `0x1100`-`0x1104` | ⬜ |
| Sonic Foundry / NCT lossless | `0x1971`, `0x1FC4` | ⬜ |
| Unknown/reserved/development tags | `0x0000`, `0x008D`, `0x0301`-`0x0308`, `0x2500` range, `0xE708`, `0xFFFF` | 👁 |

The complete 245-row factual baseline stays linked above. As aliases are implemented, add table-driven tests that prove the historical tag resolves to the canonical family **and** that container framing is valid for that tag.

## Architectural target: one codec registry, many identifiers

A future audio identifier registry should carry at least:

- canonical codec family;
- WAVE/ACM numeric tags;
- RIFF/AVI FourCC aliases where applicable;
- ISO-BMFF sample entries / object types;
- Matroska codec IDs;
- Ogg mappings;
- RealMedia identifiers;
- profile/variant metadata;
- decoder/encoder availability independently of identifier recognition.

That removes duplicated switch statements and makes the GSpot 245-tag audit a data/interop problem instead of 245 unrelated implementations.
