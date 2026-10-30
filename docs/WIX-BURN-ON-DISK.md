# WiX Burn bundles on disk

A Burn bundle is the Burn engine executable with cabinets appended. This is what the reader in
`Hawkynt.FileFormats.Archives/FileFormats/FileFormat.WixBurn` relies on, as measured on bundles that
WiX 3.14's `candle.exe` / `light.exe` built and checked against what WiX's own `dark.exe -x` extracts
from them. WiX's sources (MS-RL) were not used.

## `.wixburn` section

The engine carries a PE section named `.wixburn` (raw size 512 in the samples), little-endian:

| Offset | Size | Meaning (measured) |
| ---: | ---: | --- |
| 0 | 4 | magic `0x00F14300` |
| 4 | 4 | section version, 2 |
| 8 | 16 | bundle GUID |
| 24 | 4 | stub size: file offset of the first container |
| 28 | 4 | original checksum (0 unsigned) |
| 32 | 4 | original signature offset (0 unsigned) |
| 36 | 4 | original signature size (0 unsigned) |
| 40 | 4 | container format, 1 = cabinet |
| 44 | 4 | container count *n* |
| 48 | 4 × *n* | container sizes |

Container *i* starts at stub size plus the sizes of containers 0 … *i*−1. Every container is a
Microsoft Cabinet (`MSCF`).

## Containers and the manifest

- Container 0 holds the bootstrapper application. Its entry `0` is the Burn manifest
  (`BurnManifest` XML); the other entries are `u0`, `u1`, … .
- Attached containers (1 …) hold the chained packages and their payloads as `a0`, `a1`, … .
- In the manifest, `UX/Payload` elements map `SourcePath` (`u…`) to `FilePath`; `Payload` elements
  outside `UX` name their `Container`, and `Container` elements with `Attached="yes"` give that
  container's `AttachedIndex`. `Packaging="external"` / download payloads are not in the bundle.
- `Hash` is the payload's SHA-1 (40 hex digits) in WiX 3; the reader also accepts 64- and
  128-digit hashes (SHA-256/SHA-512) and verifies every payload against it.

## Naming

As `dark.exe -x` writes them: `UX/<FilePath>` (plus `UX/manifest.xml` for entry `0`) and
`AttachedContainer/<FilePath>`, with `\` turned into `/`.
