# ARJ reference archives

Written by the open-source ARJ port, `ARJ32 v 3.10` (Ubuntu 24.04 package `arj`), with
`generate.sh` in this directory (`arj a -m<N> -e -y mN.arj TEXT.TXT RANDOM.BIN REPEAT.BIN ONE.TXT`),
on 2026-10-19. Only the archives are checked in; the script regenerates the inputs.

| Input | Bytes | SHA-256 | Content |
|-------|------:|---------|---------|
| `TEXT.TXT` | 18,000 | `c0541069eb5a1c9d4a11c2abb3993a554ba4a272fa671ea926f035775548de8e` | 300 numbered text lines |
| `RANDOM.BIN` | 3,000 | `44763bf1aa0863dd94c239c350c8fe4aa8dc640d51fe567e94f1b956b261a46f` | LCG bytes, fixed seed |
| `REPEAT.BIN` | 6,000 | `686506c384f307f4626b9c7bb4ca22160352b499810e5673be72189117a39aa2` | `RANDOM.BIN` twice: its second half is only reachable by 3,000-byte-back matches |
| `ONE.TXT` | 1 | `ca978112ca1bbdcafac231b39a23dc4da786eff8147c4e72b9807785afee48bb` | `a` |

| Archive | Method of `TEXT.TXT` / `REPEAT.BIN` | SHA-256 |
|---------|------|---------|
| `m1.arj` | 1 | `e6ce7f28aa8f5ee6b33b35eff8f97b2b69cb36c4dd0357887e66fcdca8816215` |
| `m2.arj` | 2 | `ec81079ea9fe9745b511a0300657f956365f3b8f5bbfbc79edd07753d84fd6a8` |
| `m3.arj` | 3 | `c8abccbde2a77c79fb33c664e97e8f2935fadc48766a7185f7eb4d228a974560` |
| `m4.arj` | 4 ("fastest") | `f6d862499f4038fd5d6623fe1431a76a1f078f6f11d2570bb65c41d05d775bb7` |

ARJ stored `RANDOM.BIN` and `ONE.TXT` (method 0) in every archive, as it does with members that do
not shrink. Each member carries ARJ's CRC-32, which the reader checks.
