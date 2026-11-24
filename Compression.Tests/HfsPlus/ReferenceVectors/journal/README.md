# HFS+ volumes macOS left with a journal holding transactions

Eight 64 MiB journaled volumes (`newfs_hfs -J 1m -b 4096`, HFS+ and case-sensitive HFSX) that
macOS 26 (build 25G83) was writing when the disk-image helper was paused and the backing file
copied: the journal header still records transactions the volume does not yet carry. The capture
ran on a GitHub-hosted macOS runner; nothing here was written by CompressionWorkbench.

| | |
| --- | --- |
| Upstream | [deploymenttheory/go-apfs-v3](https://github.com/deploymenttheory/go-apfs-v3), recipe `acceptance/recipes/hfs-journal-interruption.json` |
| Release | `fixtures/2026.10.28-40647069992` (tag commit `fd6b5bad7d124e209347ef442471d059ba911b2e`), asset `fixtures-macos-26-cbe736cc307ce556.tar.gz`, SHA-256 `cbe736cc307ce556906bb8b146bc4417b9617908a42bfb60b96fc1e563652acd` |
| Files | `hfs-journal-interruption/<profile>.interrupted-<shape>.raw.gz`, copied unchanged |

The four shapes: `linear` (log between start and end, no wrap), `wrapped` (log wraps past the end
of the journal file), `complete-tail` (a whole transaction written past the recorded end) and
`incomplete-tail` (a transaction past the recorded end whose blocks were not all written).

## Expected values

The same release holds, for every volume, the bytes Apple's kernel leaves after replaying the
journal and nothing else (`sysctl` `HFS_REPLAY_JOURNAL` on the raw device, the
`<profile>.replayed-<shape>.replay-only.raw.gz` assets; identical to the volume after a full
mount). Those images are not copied; their SHA-256 is the oracle the tests check our replay
against.

| Volume | SHA-256 (uncompressed, as captured) | SHA-256 after Apple's replay |
| --- | --- | --- |
| `hfsplus-j.interrupted-linear` | `e69ab01c3c56fd2d2527a09b3be240c4c1928d4faded1399737d0e7d7c1e265f` | `0eb7b56863bd6d0df872e9f5537427e09066b2c5b5fe278bbb5ab3161939cb9d` |
| `hfsplus-j.interrupted-wrapped` | `e73a70429ce656c08bd46eb6828364d7366e4181b0c6f0a894e902f8a4b2c863` | `00692072d03c237395741f3657c0ebe39c2d3df74da82e5cdba4bc68dfbe6e50` |
| `hfsplus-j.interrupted-complete-tail` | `28a24642312f64691147f72ed87d9bb8ca6fd01bbf5056c281a63f691f4dbbab` | `9be91d32d3b286a8e3f08fe9d1f8e27d530d6d811844327058ec89ea93bdb657` |
| `hfsplus-j.interrupted-incomplete-tail` | `3dc08967e380471d5bbba15b20df175524ceb663e9a0959821705aee46300d76` | `bd14b77e624f35d6a4642c8cb8342d382bd79ab4ab6e84a04bbb887371764851` |
| `hfsx-j.interrupted-linear` | `c6ffdb1ca0e2102e799dea0e06468ff60d9d0a3affda9f0bcd41d2d19b1757ee` | `9c7bd9f7479e76dbcb49d798d242e70c35f3ff61f6b199df26b71f229fa1d814` |
| `hfsx-j.interrupted-wrapped` | `875734195de02fcd6543a818791737c2e1cd2379573f7497e33603916b22abb8` | `033ad1558cb7a93073f7110b73ac7c03adaa3999a50851a8e7e9a19ddfc3d8c0` |
| `hfsx-j.interrupted-complete-tail` | `f720ed0cdbfae34fb9a71cfa2f7759bccc5efba45e61694c34f8cc2a2c29a081` | `6fa7fdbfbcf32fbdaa71490022088253e40929a64e4865f5249d0160bbe5b9b2` |
| `hfsx-j.interrupted-incomplete-tail` | `74e14bb1842719ef2d20b6eddb100ce66eca81c15af1d31732bafc163602b866` | `68b05146cb36d0454bc0f029ae26a7ecfbff015c0d04d04f643cab287a18e5e6` |

What the replay changes, measured by comparing each pair: the blocks the transactions carry, and
in the journal header only start, end and the header checksum (start = end = where the replayed
log ends; the sequence number stays). In the `complete-tail` volumes the transaction past the
recorded end is applied and the log ends after it; in the `incomplete-tail` volumes, where one of
its blocks fails its checksum, it is not, and the log ends at the recorded end.

## Licence

go-apfs-v3: MIT License, Copyright (c) 2026 Deployment Theory.

```
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
