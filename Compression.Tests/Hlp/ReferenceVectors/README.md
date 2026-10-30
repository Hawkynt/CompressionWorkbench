# HLP reference vector

| File | Origin |
| --- | --- |
| `putty-0.60.hlp.gz` | `PUTTY.HLP` (629483 bytes, SHA-256 `5c5e6d849833b39b1d7fb46523155f7691164c0e462828650fc5918fec8b7b01`) from the PuTTY 0.60 release archive `https://the.earth.li/~sgtatham/putty/0.60/x86/putty.zip` (SHA-256 `dd86a9b6b9eb6379042f4b1a13a0039b97bc7f064381be34208d53eb54b78f89`), compiled by HCRTF; gzip-compressed here (Python `gzip`, level 9, mtime 0) to keep the repository small. Its |SYSTEM record says: "This manual is copyright 2001-2007 Simon Tatham. ... You may distribute this documentation under the MIT licence." |

helpdeco (github.com/pmachapman/helpdeco, revision `785273788af17fde4fba7df7162d547f8645d7d8`, built with gcc in
WSL) lists its internal directory with `-d` and reports each internal file's size with
`helpdeco PUTTY.HLP "|NAME"`; `HlpTests` pins those names, offsets and sizes.
