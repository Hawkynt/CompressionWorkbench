# Impulse Tracker compressed-sample reference modules

Real modules whose samples are stored IT214- or IT215-compressed, used unmodified by
`ItSampleReferenceVectorTests.cs` (libxmp digests) and `ItSampleLibopenmptOracleTests`
(libopenmpt render equivalence). Fetched on 2026-10-26.

| File | Title / author | Licence | Source | Written by (cwt) | Compressed samples |
|------|----------------|---------|--------|------------------|--------------------|
| `tute.it` | Impulse Tracker tutorial song, Jeffrey Lim | BSD-3-Clause (below) | [`jthlim/impulse-tracker`](https://github.com/jthlim/impulse-tracker) `Tutorial/TUTE.IT` @ `58c44e48dfdf479b186db17df0f2fa494b8c9aa9` | Impulse Tracker 2.14 (`0x0214`) | 8 × 8-bit IT214 |
| `1kb.it` | "1KB", NEPTUNIUM237 | Public domain | [Mod Archive #211479](https://modarchive.org/index.php?request=view_by_moduleid&query=211479) | OpenMPT 1.32 (`0x5132`) | 8-bit and 16-bit IT214 |
| `pms_are1.it` | "PMS: Romantic Area (L)", Powerack | Public domain | [Mod Archive #162054](https://modarchive.org/index.php?request=view_by_moduleid&query=162054) | Impulse Tracker 2.14v5 (`0x0217`) | 10 × 8-bit IT214, one of 64,000 samples (2 blocks) |
| `chipdisko2.it` | "LEVEL2", FunnyMan | Public domain | [Mod Archive #211243](https://modarchive.org/index.php?request=view_by_moduleid&query=211243) | OpenMPT 1.32 (`0x5132`) | 3 × 16-bit IT214, one of 82,533 samples (6 blocks) |
| `what_if_i_took_you.it` | "What if I 'took' you?", Djego Flochs | [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/) | [Mod Archive #211109](https://modarchive.org/index.php?request=view_by_moduleid&query=211109) | Impulse Tracker 2.14v5 (`0x0217`) | 3 × 8-bit and 7 × 16-bit IT215, incl. 40,000 (8-bit, 2 blocks) and 95,256 (16-bit, 6 blocks) |

MD5 of the Mod Archive files as the archive lists them: `1kb.it` `62fafad0d4485573034fb4781e93746b`,
`pms_are1.it` `7e90442adf585d1ae5710952ff72f697`, `chipdisko2.it` `9c40e2c7ea68cb9b7a7accfe1caf8c29`,
`what_if_i_took_you.it` `dcbfbf700fa1af82695e1ab3c39fc384`; `tute.it` `8730ea7fddf579cbbfbe38dfab49ccc7`.
The writer names are libxmp's reading of each file's header.

## Expected PCM: `libxmp-4.6.3.sha256.txt`

One line per compressed sample: `file index length bits it214|it215 sha256`, the SHA-256 of the
sample's PCM as signed little-endian 8- or 16-bit values. Produced by
[libxmp](https://github.com/libxmp/libxmp) 4.6.3 (MIT), built from the release tarball with
MinGW-w64 GCC 16.2 (`cmake -G "MinGW Makefiles" -DBUILD_SHARED=ON`), loading each module with
`xmp_load_module` and hashing `xmp_module.xxs[i].data` through its public API (Python `ctypes`).
libxmp was used as a black box only; its sources were not read.

Beyond these files, the decoder was checked against libxmp on 305 further compressed samples (other
Mod Archive modules and libxmp's own test modules, not checked in): 338 of 338 identical. libopenmpt
agrees with every checked-in sample through the render-equivalence test.

## Licences

`tute.it` — Copyright (c) 2024, Jeffrey Lim. Redistribution and use in source and binary forms, with
or without modification, are permitted provided that the following conditions are met:
(1) Redistributions of source code must retain the above copyright notice, this list of conditions
and the following disclaimer. (2) Redistributions in binary form must reproduce the above copyright
notice, this list of conditions and the following disclaimer in the documentation and/or other
materials provided with the distribution. (3) Neither the name of the copyright holder nor the names
of its contributors may be used to endorse or promote products derived from this software without
specific prior written permission. THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND
CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO
EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED
AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE
OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY
OF SUCH DAMAGE.

`what_if_i_took_you.it` — "What if I 'took' you?" by Djego Flochs, licensed under
[Creative Commons Attribution 4.0](https://creativecommons.org/licenses/by/4.0/), unmodified.

`1kb.it`, `pms_are1.it`, `chipdisko2.it` — released to the public domain by their authors on
The Mod Archive.
