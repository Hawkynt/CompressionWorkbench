# OCFS2 reference volume

| File | Made by | What it is |
|------|---------|------------|
| `linux-indexed-dirs.img.gz` | `mkfs.ocfs2 -q -M local -b 4096 -C 4096 -N 1 -L cwbdx` (ocfs2-tools, Ubuntu 24.04, default features: `indexed-dirs` among them) on a 64 MiB file, then the Ubuntu 24.04 kernel's `ocfs2` driver through the libguestfs appliance: `mkdir /small /big`, 100 files `small/file-with-a-longish-name-<i>.txt` holding `s<i>\n`, 1000 files `big/file-with-a-longish-name-<i>.txt` holding `b<i>\n` | Two indexed directories written by the kernel: `small` (2 blocks, the index inline in its root) and `big` (11 blocks, the index in 6 leaf clusters, 1002 entries). `fsck.ocfs2 -fn` passed it. Captured 2026-11-24. |

The recipe is `ocfsdx.sh`-style: format, mount through `guestfish`, write the files in a
`debug sh` loop under `/sysroot`, unmount. Compressed with `gzip -9 -n`; expands to 67 108 864 bytes.
CompressionWorkbench wrote none of these bytes.
