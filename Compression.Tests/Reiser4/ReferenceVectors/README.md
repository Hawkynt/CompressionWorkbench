# Reiser4 reference volumes

Volumes a Reiser4 kernel wrote, checked in so the reader and editor tests run without Linux tools.
`generate.sh` documents how they were made; it is not a build input.

Each volume was formatted by `mkfs.reiser4 -fffy` (reiser4progs 1.2.2, 16 MiB, UUID
`11111111-2222-3333-4444-555555555555`) with the plugin options below, then filled by `populate.sh`
inside the kernel — Metztli's Debian build of Linux 5.4.18 with the Reiser4 SFRN 4.0.2 patch
(`linux-image-5.4.0-3+reizer4.0.2-cloud-amd64`), booted under QEMU by
`../KernelOracle/kernel-run.sh`. The tree holds files of 0 to 200 705 bytes (random, and a 69 559-byte text that spans two 64 KiB clusters), nested
and empty directories, names of 35, 46 and 255 bytes, names ending in `.c`, `.o` and `.abc`,
a short and a 423-byte symlink, a file with three names, a FIFO, character device 1:3, block device
7:0, a file with a hole, a 300-entry directory, an owner of 1234:5678 with mode 0751, and a
modification time in 2001.

| File | mkfs.reiser4 options | What it exercises |
|------|------------------|-------------------|
| `default` | — | cryptcompress files (lzo1, conv mode, 64 KiB clusters), the kernel turning incompressible ones into extents |
| `gzip1-force-8k` | `compress=gzip1,compressMode=force,cluster=8K` | raw-deflate clusters with their Adler-32 |
| `zstd1-latt-16k` | `compress=zstd1,compressMode=latt,cluster=16K` | Zstandard clusters |
| `lzo1-none-4k` | `compressMode=none,cluster=4K` | uncompressed clusters |
| `reg40-smart` | `create=reg40` | tails up to four blocks, extents above |
| `reg40-tails` | `create=reg40,formatting=tails` | tails only |
| `reg40-node41` | `create=reg40,formatting=extents,node=node41` | checksummed nodes, extents only |
| `node41` | `node=node41` | checksummed nodes with cryptcompress files |
| `tea-lexic` | `create=reg40,hash=tea_hash,fibration=lexic_fibre` | TEA-hashed long names, no fibres |
| `fnv1-dot-o` | `create=reg40,hash=fnv1_hash,fibration=dot_o_fibre` | FNV-1 long names, `.o` fibre |
| `deg-ext-3` | `create=reg40,hash=deg_hash,fibration=ext_3_fibre` | degenerate hash, three-letter extension fibres |
| `rupasov` | `create=reg40,hash=rupasov_hash` | read only: `fsck.reiser4` disagrees with the kernel's Rupasov keys |
| `journal-replay` | `create=reg40` | a commit the kernel had not yet written back: block 19 names transaction 181, block 20 transaction 130 |

`<name>.manifest` is what the kernel read back after unmounting and mounting again: one line per
object, `path|type|mode|uid|gid|size|nlink|mtime|major:minor|md5-or-link-target`, every regular file
read whole. For `journal-replay` the kernel replayed the journal at mount, so the manifest is the
post-replay state. `default` and `node41` list two fewer `many/` files than the others and
`gzip1-force-8k` one fewer: the kernel itself did not keep them, and the reader agrees with what it
did keep.

Every volume except `rupasov` passed `fsck.reiser4 --check` (`journal-replay` after the replay that
check performs). Captured 2026-10-19 on Ubuntu 24.04 under WSL2 (the WSL kernel has no Reiser4
driver; the QEMU guest is the only writer). Compressed with `gzip -9 -n`.
