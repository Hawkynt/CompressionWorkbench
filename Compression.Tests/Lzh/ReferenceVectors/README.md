# LHA reference archives

Archives written by other LHA implementations, for the methods our writer cannot produce.

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
