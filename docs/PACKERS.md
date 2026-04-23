# CompressionWorkbench Executable Packer Coverage

Snapshot: 2026-04-23.

The toolkit detects and (where possible) decompresses common executable
compressors and protectors — UPX, the demoscene 4K/64K compressors, and the
DOS/Win32 packers from the 1990s/2000s. Detection layers are designed to
survive tampering: section-name renames, wiped magic strings, and stub
patches that anti-detection tools commonly apply.

## UPX (Ultimate Packer for eXecutables)

| Capability                            | State | Notes                                                                                                                                                                                                                          |
| ------------------------------------- | ----- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| Detection (canonical UPX)             | YES   | Section names UPX0/UPX1/UPX2; "UPX!" packer-header magic; "$Info: This file is packed with the UPX..." tooling banner.                                                                                                         |
| Detection (tampered)                  | YES   | Brute-force PackHeader scan validates `format`/`method`/`uLen`/`cLen`/`level`/`version` even with magic wiped. Structural fingerprint requires BSS-style first section (`RawSize=0` + `VirtualSize>0`) as a hard prerequisite. |
| Decompression — NRV2B_LE32 (method 2) | YES   | In-process via `BB_Nrv2b`.                                                                                                                                                                                                     |
| Decompression — NRV2D_LE32 (method 3) | YES   | In-process via `BB_Nrv2d` (UCL-spec port).                                                                                                                                                                                     |
| Decompression — NRV2E_LE32 (method 8) | YES   | In-process via `BB_Nrv2e` (UCL-spec port).                                                                                                                                                                                     |
| Decompression — NRV2B_LE16 (method 4) | YES   | `Nrv2bBuildingBlock.DecompressRawLe16`.                                                                                                                                                                                        |
| Decompression — NRV2D_LE16 (method 5) | YES   | `Nrv2dBuildingBlock.DecompressRawLe16`.                                                                                                                                                                                        |
| Decompression — NRV2B_8 (method 6)    | YES   | `Nrv2bBuildingBlock.DecompressRawByte`.                                                                                                                                                                                        |
| Decompression — NRV2D_8 (method 7)    | YES   | `Nrv2dBuildingBlock.DecompressRawByte`.                                                                                                                                                                                        |
| Decompression — NRV2E_LE16 (method 9) | YES   | `Nrv2eBuildingBlock.DecompressRawLe16`.                                                                                                                                                                                        |
| Decompression — NRV2E_8 (method 10)   | YES   | `Nrv2eBuildingBlock.DecompressRawByte`.                                                                                                                                                                                        |
| Decompression — LZMA (method 14)      | YES   | Via `BB_Lzma`.                                                                                                                                                                                                                 |
| Decompression — DEFLATE (method 15)   | NO    | Rare in UPX output; deferred.                                                                                                                                                                                                  |
| PE header reconstruction              | NO    | Surfaces `decompressed_payload.bin` as a raw blob; IAT reconstruction + OEP restoration is delegated to `upx -d`.                                                                                                              |

The detection result is exposed in `metadata.ini` as a 3-tier confidence
(`None` / `Heuristic` / `Confirmed`) plus the full evidence record so callers
can distinguish a clean UPX hit from a structural-only suspicion.

## Demoscene + historical packers

These are detection-only — full decompression requires the original tool's
runtime stub or a bespoke decompressor that we haven't ported yet.

| Packer             | Container            | Detection signature                                                           | Description                                                                                    |
| ------------------ | -------------------- | ----------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------- |
| **PKLITE**         | DOS .exe             | `"PKLITE Copr."` / `"PKlite Copr."` in first 1 KB                             | PKWARE's commercial DOS exe compressor (1990); reports version byte + extra-info flag.         |
| **LZEXE**          | DOS .exe             | `LZ91` / `LZ09` signature in first 1 KB                                       | Fabrice Bellard's DOS exe compressor (1989); reports version 0.91 / 0.90.                      |
| **Petite**         | Win32 PE             | `.petite*` section name OR `Petite` literal                                   | Ian Luck's late-90s PE compressor; reports section name + presence of literal.                 |
| **Shrinkler**      | Amiga HUNK           | AmigaOS HUNK_HEADER magic (`0x000003F3`) + `Shrinkler` literal in first 64 KB | Blueberry's range-coding/context-mixing compressor for Amiga 4K and 64K demoscene productions. |
| **FSG**            | Win32 PE             | `FSG!` magic in first 16 KB                                                   | "Fast Small Good" PE packer (early 2000s).                                                     |
| **MEW**            | Win32 PE             | Section name beginning with `.MEW` / `MEW`                                    | Northfox's tiny PE packer.                                                                     |
| **MPRESS**         | Win32 PE / Linux ELF | `.MPRESS1` / `.MPRESS2` section name OR `MPRESS` / `MATCODE` literal          | MATCODE's PE/ELF compressor.                                                                   |
| **Crinkler**       | Win32 PE             | `Crinkler` / `crinkler` literal anywhere                                      | Mentor & Blueberry's 4K Windows demoscene compressor.                                          |
| **kkrunchy**       | Win32 PE             | `kkrunchy` literal anywhere                                                   | Farbrausch's 64K Windows demoscene compressor (used by .kkrieger, fr-08).                      |
| **ASPack**         | Win32 PE             | `.aspack` / `.adata` section OR `ASPack` literal                              | Solodovnikov's classic PE packer (early 2000s).                                                |
| **NsPack**         | Win32 PE             | `.nsp*` section name OR `NsPack` literal                                      | LiuXingPing's PE packer.                                                                       |
| **Yoda's Crypter** | Win32 PE             | `.yC` / `yC` section OR `Yoda's` literal                                      | Classic anti-RE crypter.                                                                       |
| **ASProtect**      | Win32 PE             | `ASProtect` literal anywhere                                                  | Commercial PE protector (ASPack's bigger sibling). Disambiguated from ASPack by the literal.   |
| **Themida**        | Win32 PE             | `Themida` / `WinLicense` literal                                              | Oreans commercial protector. Best-effort — stronger protection settings strip the literal.     |
| **VMProtect**      | Win32 PE             | `.vmp*` section OR `VMProtect` literal                                        | Commercial virtualizing protector. Best-effort — newer 2.x builds strip both.                  |

For each detector the descriptor surfaces a `metadata.ini` (with format-
specific fields like detected version, signature offset, packer-header
fields), an `mz_header.bin` / `hunk_header.bin` snapshot (when applicable),
and `packed_payload.bin` (the original file as-is, ready to be piped through
the packer's reference decoder).

## UCL-family building blocks

| BB ID      | Description                                                              | Status                                  |
| ---------- | ------------------------------------------------------------------------ | --------------------------------------- |
| `BB_Nrv2b` | UCL NRV2B LE32 — LZ77 + interleaved variable-length integer bit stream   | Spec-faithful (UPX-compatible decoder). |
| `BB_Nrv2d` | UCL NRV2D LE32 — three-bit-per-iter offset varint + low-bit length tying | Spec-faithful (UPX-compatible decoder). |
| `BB_Nrv2e` | UCL NRV2E LE32 — entropy-refined NRV2D variant                           | Spec-faithful (UPX-compatible decoder). |
| `BB_Lzma`  | LZMA dictionary compressor                                               | Pre-existing.                           |

Each emits a 4-byte little-endian uncompressed-size header so the building
block can round-trip standalone via `IBuildingBlock.Compress`/`Decompress`.
`Nrv2{b,d,e}BuildingBlock.DecompressRaw(compressed, exactOutputSize)` static
helpers are available for callers reading bare streams (UPX payloads, OS/2
drivers, retro-computing software collections) without the size header.

## Detection confidence model

UPX detection returns a `DetectionConfidence` enum:

- `None` — no evidence. Descriptor's `List` / `Extract` throws so
  `FormatDetector` can fall back to plain `PeResources` / `Elf`.
- `Heuristic` — structural fingerprint match (BSS-style first section + RWX
  flags + entry in last section + high entropy) but no PackHeader. The
  binary is plausibly UPX-packed but a human/external tool should confirm.
- `Confirmed` — PackHeader found (with or without intact magic), or
  canonical section names present, or the tooling banner is intact.

The evidence record exposes every contributing signal so users can audit
why a binary was flagged.
