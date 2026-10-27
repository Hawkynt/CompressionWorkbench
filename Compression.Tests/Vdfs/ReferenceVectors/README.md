# VDF reference volumes

Volumes from software other than ours, and ZenKit's listings of volumes we wrote. They are
embedded into the test assembly (`VdfsVectors.*`) and checked by `VdfsReferenceVectorTests`.

## Volumes from the reference tools

The first two come from the test corpus of GothicKit's ZenKit
(<https://github.com/GothicKit/ZenKit>, `tests/samples/`, commit
`e1c1044e4e0708b05db152ede44393f78d935ad5`). ZenKit is MIT-licensed; its notice is in
`LICENSE.ZenKit.md`. The licence texts inside them are verbatim GNU and MIT licence
files, which their licences allow anyone to redistribute.

| File | Written by | Holds |
|------|-----------|-------|
| `basic.vdf` | GothicVDFS (the openzen sample, 2021-04-27) | stored volume, 5 files in 3 directory levels |
| `basic_zipped.vdf` | VDFSTool, per ZenKit PR #124 | the same files zipped (flags `0xA0`, quarter-file blocks, zlib level 9) |
| `zenkit_stored.vdf` | ZenKit `Vfs::save` | `SampleTree()` in `VdfsReferenceVectorTests` |
| `zenkit_zipped.vdf` | ZenKit `Vfs::save_compressed` | the same, zipped (8 KiB blocks, sound raw) |

## Volumes we wrote, and ZenKit's reading of them

| File | Written by | Checked by |
|------|-----------|-----------|
| `cwb_stored.vdf` | our `VdfsWriter`, `SampleTree()` | `cwb_stored.zenkit.tsv` |
| `cwb_zipped.vdf` | our `VdfsWriter { Zipped = true }`, `SampleTree()` | `cwb_zipped.zenkit.tsv` |

`basic.zenkit.tsv` and `basic_zipped.zenkit.tsv` are ZenKit's listings of the two corpus
volumes.

## The oracle

Every `*.zenkit.tsv` and both `zenkit_*.vdf` came from `vdforacle`, a 70-line driver over
ZenKit's public `Vfs` API. It was built from the commit above with `ZK_ENABLE_ZIPPED_VDF=ON`
(miniz 3.1.1) using MinGW-w64 g++ on Windows:

* `vdforacle list <vdf>` mounts the volume with `Vfs::mount_disk`, walks it, decodes every
  file through `VfsNode::open_read`, and prints one line per entry: `D<TAB>path`, or
  `F<TAB>path<TAB>size<TAB>fnv1a64` of the decoded bytes.
* `vdforacle pack <dir> <vdf> [zipped]` mounts a host directory and saves it with
  `Vfs::save` or `Vfs::save_compressed` (Gothic 2 signature, Unix time 1600000000).

ZenKit could not have produced the right hash for every file without parsing our header,
walking our catalog, and inflating every ZippedStream block.
