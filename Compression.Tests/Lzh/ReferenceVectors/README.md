# LHA reference archives

Archives written by other LHA-family archivers, for methods and header levels our writer does not
produce or that must read exactly as their archiver wrote them.

| File | Method | Written by | Taken from |
|------|--------|------------|------------|
| `lh2.lzh` | `-lh2-`, level-2 header | UNLHA32.DLL 2.67a (`a archive.lzh filename`, switch `-jm5`) | lhasa `test/archives/unlha32/lh2.lzh` |
| `lh3.lzh` | `-lh3-`, level-2 header | UNLHA32.DLL 2.67a (switch `-jm6`) | lhasa `test/archives/unlha32/lh3.lzh` |

Both come from the test suite of lhasa (<https://github.com/fragglet/lhasa>, ISC licence,
Copyright (c) 2011-2025 Simon Howard), fetched from `master` on 2026-10-19:

| File | SHA-256 |
|------|---------|
| `lh2.lzh` | `16e1cba85552ddc930442874dbfd42c0042547da482793e31ac2aaf819dc765e` |
| `lh3.lzh` | `095f5675a1224db59db9f3c3075b234c2b62ee8710e34968882fccae4659e63e` |

Each holds one member, `LICENSE.MIT` (2,853 bytes, an MIT licence text). lhasa itself does not
decode either method, so the oracle is the member's CRC-16 recorded by UNLHA32 in the header: the
decoded bytes must match it, along with the size and the text's shape.

## lhasa suite archives

Taken from `test/archives/` of lhasa at `75ed83559f23e9538e0045c62f53f77ab03d03d6` (2026-06-17), renamed
`<directory>_<file>`:

| File | Written by | What it covers | SHA-256 |
|------|------------|----------------|---------|
| `lharc113_lh1.lzh` | LHarc 1.13 | `-lh1-`, level 0 | `594af58cb27d6a80e9bebd108cc82a8ab881a4b5549e4fc10379fbf7a0d83069` |
| `larc333_lz5.lzs` | LArc 3.33 | `-lz5-` | `ebacc5d6fa4f35ef4e2ff7176e9867e410e4dfc5922b5bcfb804185954e1ab89` |
| `larc333_initial.lzs` | LArc 3.33 | `-lz5-` copies from the predefined initial ring | `3d1a8bb82174986a0e4581d83763f22c5bdae42f5a34eb4841bdafb2f14a6f3e` |
| `generated_lzs.lzs` | lhasa's generated `-lzs-` set | `-lzs-` | `a7a0ff6acfef86a49300e4f0dcfbe7a61ba21d05b1585809e5a393ce5635c3b3` |
| `pmarc124_pm1.pma` | PMarc 1.24 | `-pm1-` | `13dfdbd992c2fac4964b3028c646aa68eb7a4a941a4224c4837058c3771b6346` |
| `pmarc2_pm2.pma` | PMarc 2 | `-pm2-` | `4428e8d9d4e5f61aa7fd041fc6048cfdb64052eaf2776f8b23b9430f506e7979` |
| `lha_amiga_122_level1.lzh` | LHA (Amiga) 1.22 | level 1 with directory extended headers | `dae46e78a2709eb664abfb904e859a653d178f53abfac7255afec6e3fe2c340f` |
| `lha_amiga_122_lh0_dirs_bug.lzh` | LHA (Amiga) 1.22 | directories written as `-lh0-` | `6aa959e8cde1419925d3e85b95ff16b2963301df114fc8f2137c7af6b8f4f17a` |
| `lha_os2_208_h3_lh5.lzh` | LHA (OS/2) 2.08 | level 3 header | `d980d1ddc47323fc860b0fb7829d72ce26afbe1f3f59f89e52f0eda54f425212` |
| `lha_os2_208_h3_subdir.lzh` | LHA (OS/2) 2.08 | level 3 with directories | `201051c47213d0964c596d40f574486dbb683665b2ba2a27a81bae69e3a9b655` |
| `unlha32_h2_lhx.lzh` | UNLHA32.DLL 2.67a | `-lhx-` (1 MiB window), level 2 | `c9ab661c5502714f9556be89617bddff237e0e21e72657dc019707aed9751d9b` |

The oracle for each member is the CRC-16 its archiver recorded. Several hold the same GPL-2 text, and
the tests check that `-lh1-`, `-lz5-`, `-lzs-`, `-lh5-` and `-lhx-` all decode it identically.

ISC licence (lhasa):

```
Permission to use, copy, modify, and/or distribute this software
for any purpose with or without fee is hereby granted, provided
that the above copyright notice and this permission notice appear
in all copies.

THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL
WARRANTIES WITH REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE
AUTHOR BE LIABLE FOR ANY SPECIAL, DIRECT, INDIRECT, OR
CONSEQUENTIAL DAMAGES OR ANY DAMAGES WHATSOEVER RESULTING FROM
LOSS OF USE, DATA OR PROFITS, WHETHER IN AN ACTION OF CONTRACT,
NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF OR IN
CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.
```
