# VMDK reference disks

Disks qemu-img and qemu-io wrote, gzip-compressed (`gzip -9 -n`) only to keep the repository
small. `generate.sh` documents how they were made; it is not a build input. The tests unpack
them to a temporary directory, because a descriptor names its extent files and a delta disk
its parent by file name.

| File(s) | What it is |
|---------|------------|
| `stream.vmdk` | streamOptimized: deflate-compressed grains behind grain markers, grain directory in the footer |
| `sparse.vmdk` | monolithicSparse |
| `flat.vmdk` + `flat-flat.vmdk` | monolithicFlat: a text descriptor naming one flat extent |
| `delta.vmdk` over `base.vmdk` | a delta disk (parentCID / parentFileNameHint) with two writes, one covering part of a grain |
| `zeroed.vmdk` | monolithicSparse with `zeroed_grain=on` and zero-writes over allocated grains |
| `split.vmdk` + `split-s001.vmdk`, `split-s002.vmdk` | twoGbMaxExtentSparse: a 3 GiB disk in two sparse extents, written across the seam |
| `manifest.txt` | SHA-256 and length of what `qemu-img convert -O raw` made of each disk |

Made by qemu-img / qemu-io 8.2.2 (Debian 1:8.2.2+ds-0ubuntu1) on Ubuntu under WSL2,
captured 2026-10-23.
