# WiX Burn reference bundle

`burn-v3.exe` was built by the WiX Toolset 3.14.1 (`wix314-binaries.zip` from
https://github.com/wixtoolset/wix3/releases/tag/wix3141rtm) `candle.exe` / `light.exe` with the
WixBalExtension standard bootstrapper application, never by CompressionWorkbench, and never run:
`python make-fixtures.py <workdir> <wix bin dir> burn-v3`. It chains one ExePackage (512 bytes of
placeholder data) with three payloads (a short text, 3000 seeded random bytes, 32000 bytes of a
repeated pattern). Stored gzip-compressed, otherwise unchanged.

`oracle.tsv` is what WiX 3.14's own `dark.exe -x` extracts from it: path, size and SHA-256.

The bundle contains the Burn engine and `wixstdba.dll`, binaries of the WiX Toolset, distributed
under the Microsoft Reciprocal License (MS-RL), https://opensource.org/licenses/MS-RL ; their source
is at https://github.com/wixtoolset/wix3 .
