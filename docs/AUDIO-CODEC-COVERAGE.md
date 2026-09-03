# Audio codec support

This ledger tracks **audio codecs and codec identifiers** independently of the container that carries them.
The package-level implementation inventory in [`Hawkynt.FileFormats.Audio/README.md`](../Hawkynt.FileFormats.Audio/README.md)
describes checked-in codec projects. This ledger deliberately records codec implementation and WAVE routing as
separate capabilities; a matching numeric tag is not evidence that two payload framings are interchangeable.

## Coverage sources

- [IANA WAVE/AVI Codec Registries (historic registry)](https://www.iana.org/assignments/wave-avi-codec-registry/wave-avi-codec-registry.xhtml)
  and [RFC 2361](https://www.rfc-editor.org/rfc/rfc2361) are the primary registration sources.
- [GSpot v2.70a audio codecs](https://gspot.headbands.com/audiocodecs.htm) and its
  [mirror](https://www.headbands.com/gspot/audiocodecs.htm) remain useful historical alias catalogues.
- Microsoft WAVE documentation and format-specific standards define framing/extra-data semantics.
- FFmpeg, libgsm and other independent implementations may be used as **behavioral oracles**. Their implementation
  code is not copied when license compatibility is unclear; public bitstream behavior is independently implemented
  and pinned with original nUnit tests.

Two previous ledger assumptions were wrong and are now corrected:

- `0x0039` is **Roland RDAC** in the IANA/RFC registry, not an IMA/DVI alias.
- `0x0130`-`0x0135` is not one SIPR bucket: `0x0133`/`0x0134` are G.729/G.729A registrations and
  `0x0135` is Kelvin.

The GSpot table must **not** become 245 decoder projects. Many entries are aliases, vendor registrations, transport
wrappers, or several tags for the same codec family. We implement a family once and map only registrations whose
framing/profile has actually been validated.

## States

| Mark | Meaning |
| --- | --- |
| ✅ | Complete for the claimed direction/profile. |
| 🟨 | Partial/profile-limited, or only a subset of the listed aliases is routed. |
| 👁 | Identifier is name-resolved but has no validated codec/container route. |
| ⬜ | Missing. |
| — | Not a WAVE/ACM identifier or not applicable. |

## GSpot WAVE/ACM family mapping

| Family | GSpot-era identifiers | Codec read | Codec write | WAVE read | WAVE write | Notes |
| --- | --- | :---: | :---: | :---: | :---: | --- |
| PCM | `0x0001` | ✅ | ✅ | ✅ | ✅ | Integer PCM. |
| IEEE float | `0x0003` | ✅ | ✅ | ✅ | ✅ | 32/64-bit IEEE float. |
| G.711 A-law | `0x0006`, `0x0102` | ✅ | ✅ | ✅ | 🟨 | Both tags decode; writer emits standard `0x0006`. |
| G.711 μ-law | `0x0007` | ✅ | ✅ | ✅ | ✅ | Standard WAVE route. |
| Microsoft ADPCM | `0x0002` | ✅ | ✅ | ✅ | ✅ | Block encoder/decoder with `fact` trimming. |
| IMA/DVI ADPCM | `0x0011` | ✅ | ✅ | ✅ | ✅ | `0x0039` was removed: it is Roland RDAC. |
| Roland RDAC | `0x0039` | ⬜ | ⬜ | 👁 | 👁 | Registration recognized; no RDAC codec yet. |
| OKI/Dialogic ADPCM | `0x0010`, `0x0017` | ✅ | ✅ | ✅ | ✅ | Validated 4-bit mono route for both registrations. |
| Creative ADPCM | `0x0200` | 🟨 | ⬜ | 👁 | 👁 | VOC decode exists; there is no checked-in Creative ADPCM encoder. |
| GSM 06.10 | `0x0031`, `0x0086`, `0x00A1`, `0x0155` | ✅ | ✅ | 🟨 | 🟨 | Standard `0x0031` now uses real Microsoft WAV49: 65-byte / 320-sample blocks. Vendor aliases remain identifier-only. |
| TrueSpeech | `0x0022` | ✅ | ⬜ | ✅ | ⬜ | Decoder only. |
| G.721 / G.726 ADPCM | `0x0040`, `0x0045`, `0x0064`, `0x0085`, `0x008B`, `0x0140`, `0x4243`, `0xA105`, `0xA107` | ✅ | ✅ | 🟨 | 🟨 | `0x0040`, `0x0045`, `0x0064` are validated; vendor aliases remain profile/framing work. |
| G.722 | `0x0065` | ✅ | ✅ | ✅ | ✅ | 64 kbit/s mode is routed for historical APICOM `0x0065`; modern FFmpeg `0x028F` is routed too. 48/56 kbit/s modes remain a codec gap. |
| G.723.1 | `0x0059`, `0x0093`, `0x00A3`, `0x0123`, `0x1C0C`, `0xA100` | ✅ | ⬜ | 👁 | 👁 | Decoder exists; historical WAVE framing still needs exact per-registration validation. |
| G.729 family | `0x0044`, `0x0083`, `0x008C`, `0x0133`, `0x0134`, `0xA103` | ⬜ | ⬜ | 👁 | 👁 | G.729/G.729A implementation required. |
| MPEG-1/2 Audio | `0x0050`, `0x0055`, `0x0700` | ✅ | 🟨 | 👁 | 👁 | Decoder covers Layers I/II/III; checked-in encoder is Layer III. WAVE/AVI framing remains separate. |
| AAC | `0x00B0`, `0x00FF`, `0x0180`, `0x0AAC`, `0x4143`, `0x706D`, `0xA106`, RealAudio AAC registrations | 🟨 | 🟨 | 👁 | 👁 | AAC-LC exists; aliases are not treated as profile-equivalent without extradata validation. |
| AC-3 | `0x0092`, `0x0241`, `0x2000` | 🟨 | 🟨 | 👁 | 👁 | Raw AC-3 and S/PDIF encapsulation must remain distinct. |
| DTS | `0x0008`, `0x0190`, `0x2001` | ✅ | 🟨 | 👁 | 👁 | Core codec exists; wrapper/tag semantics still need explicit routing. |
| ATRAC / ATRAC3 | `0x0063`, `0x0272` and Sony variants | 🟨 | ⬜ | 👁 | 👁 | `Codec.Atrac1` / `Codec.Atrac3`; exact tag-to-generation mapping pending. |
| WMA v1 | `0x0160` | ✅ | ⬜ | 👁 | 👁 | Decoder exists. |
| WMA v2 | `0x0161` | ✅ | ⬜ | 👁 | 👁 | Decoder exists. |
| WMA Pro | `0x0162`, `0x0164` | ✅ | ⬜ | 👁 | 👁 | S/PDIF registration remains separate. |
| WMA Lossless | `0x0163` | ✅ | ⬜ | 👁 | 👁 | Decoder exists. |
| RealAudio 14.4 | `0x2002` | ✅ | ⬜ | 👁 | 👁 | `Codec.Ra144`; RealMedia framing should own the normal route. |
| RealAudio 28.8 | `0x2003` | 🟨 | ⬜ | 👁 | 👁 | Current decoder remains profile-limited. |
| RealAudio Cook | `0x2004` | 🟨 | ⬜ | 👁 | 👁 | `Codec.Cook`; RealMedia integration/profile audit pending. |
| RealAudio AAC/AAC+ | `0x2006`, `0x2007` | 🟨 | 🟨 | 👁 | 👁 | Canonical AAC family, but RealAudio framing/profile rules are separate. |
| SIPR / ACELP.NET | `0x0130`-`0x0132`, Vivo/SIREN-related registrations | 🟨 | ⬜ | 👁 | 👁 | `0x0133`/`0x0134` are G.729; `0x0135` is Kelvin. |
| Vorbis ACM | `0x564C`, `0x674F`, `0x6750`, `0x6751`, `0x676F`, `0x6770`, `0x6771` | ✅ | ✅ | 👁 | 👁 | Native Vorbis R/W exists; legacy ACM framing remains separate. |
| Speex ACM | `0xA109` | ✅ | ⬜ | 👁 | 👁 | Decoder exists; WAVE route pending. |
| WavPack | `0x5756` | ✅ | ✅ | 👁 | 👁 | Native WavPack R/W exists; WAVE-tag route is separate. |
| FLAC | `0xF1AC` | ✅ | ✅ | 👁 | 👁 | Native FLAC R/W exists; WAVE-tag route is separate. |
| AMR-NB | `0x0136`, `0x4201`, `0x7A21`, `0x7A22` | ✅ | ✅ | 👁 | 👁 | Raw AMR R/W exists; WAVE aliases remain framing work. |
| AMR-WB | `0xA104` | ✅ | ✅ | 👁 | 👁 | Codec R/W exists; WAVE alias remains framing work. |
| IBM CVSD / CVSD family | `0x0005` | ✅ | ✅ | 👁 | 👁 | Generic `Codec.Cvsd` is R/W; RIFFNEW's IBM profile parameters differ, so `0x0005` is not force-aliased. |
| MACE 3:1 / 6:1 | QuickTime identifiers | ✅ | ⬜ | — | — | `Codec.Mace`; QuickTime/container identifier work belongs in the same registry later. |
| Musepack | container-native | ✅ | ⬜ | — | — | `Codec.Musepack` SV7/SV8. |
| Monkey's Audio | container-native | 🟨 | 🟨 | — | — | Supported levels remain profile-limited. |
| TTA | container-native | ✅ | ✅ | — | — | `Codec.Tta`. |
| Shorten | container-native | ✅ | ✅ | — | — | `Codec.Shorten`. |

## GSpot long-tail gaps

The remaining explicit tags/ranges are kept visible even where no decoder exists. Identifier coverage is not confused
with decode/encode support.

| Historical family | Representative GSpot tags | Read | Write | Identified |
| --- | --- | :---: | :---: | :---: |
| VSELP / Microsoft speech | `0x0004`, `0x0032`, `0x0066`, `0x0067`, `0x0082` | ⬜ | ⬜ | 👁 |
| Voxware speech/music family | `0x0062`, `0x0069`-`0x007B`, `0x0081`, `0x181C` | ⬜ | ⬜ | 👁 |
| Sonarc / PAC / proprietary music coders | `0x0021`, `0x0053`, `0x181E`, `0x1500` | ⬜ | ⬜ | 👁 |
| APT-X / legacy studio codecs | `0x0025`, `0x0099` | ⬜ | ⬜ | 👁 |
| Philips speech | `0x0098`, `0x0120`, `0x0121` | ⬜ | ⬜ | 👁 |
| Qualcomm PureVoice/HalfRate | `0x0150`, `0x0151` | ⬜ | ⬜ | 👁 |
| Intel Music Coder / Indeo Audio | `0x0401`, `0x0402` | ⬜ | ⬜ | 👁 |
| QDesign Music | `0x0450` | ⬜ | ⬜ | 👁 |
| On2 audio registrations | `0x0500`, `0x0501` | ⬜ | ⬜ | 👁 |
| Olivetti / Lernout & Hauspie speech families | `0x1000`-`0x1004`, `0x1100`-`0x1104` | ⬜ | ⬜ | 👁 |
| Sonic Foundry / NCT lossless | `0x1971`, `0x1FC4` | ⬜ | ⬜ | 👁 |
| Unknown/reserved/development tags | `0x0000`, `0x008D`, `0x0301`-`0x0308`, `0x2500`, `0xE708`, `0xFFFF` | — | — | 👁 |

The 245-row GSpot baseline stays linked above. New aliases need table-driven tests proving both canonical-family resolution
and valid container framing before their WAVE read/write columns can turn green.

## Implemented in the current WAVE route

The bidirectional WAVE adapter currently validates and writes these compressed families:

- G.711 A-law/μ-law;
- Microsoft ADPCM;
- IMA/DVI ADPCM;
- OKI/Dialogic 4-bit ADPCM (`0x0010`, `0x0017`);
- Microsoft GSM 06.10 WAV49 (`0x0031`);
- G.721 (`0x0040`);
- G.726 16/24/32/40 kbit/s (`0x0045`, `0x0064`);
- G.722 64 kbit/s (`0x0065`, plus modern FFmpeg `0x028F`).

`fact` sample counts trim block/bit padding back to the original frame count.

## Architectural target: one codec registry, many identifiers

A shared audio identifier registry remains the architectural target. It should carry canonical families plus historical
WAVE/ACM registrations with read/write availability separated at both levels, then grow to carry:

- RIFF/AVI FourCC aliases where applicable;
- ISO-BMFF sample entries / object types;
- Matroska codec IDs;
- Ogg mappings;
- RealMedia identifiers;
- QuickTime codec identifiers;
- profile/variant/extradata metadata.

That removes duplicated switch statements and turns the historic catalogue into a data/interoperability problem rather
than hundreds of unrelated implementations.
