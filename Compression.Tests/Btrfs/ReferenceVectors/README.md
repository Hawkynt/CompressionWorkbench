# btrfs compressed-extent reference images

Volumes whose files the Linux kernel's own btrfs driver wrote, checked in so the reader
tests run without Linux tools. `generate.sh` documents how they were made; it is not a
build input.

| File | What it is |
|------|------------|
| `btrfs-zlib.img.gz` | 128 MiB volume, files written with `compress-force=zlib` |
| `btrfs-lzo.img.gz` | the same tree written with `compress-force=lzo` |
| `btrfs-zstd.img.gz` | the same tree written with `compress-force=zstd` |
| `manifest.txt` | SHA-256, length and path of every file, as the kernel read them back from each image (the three agree) |
| `extents.txt` | `btrfs inspect-internal dump-tree -t fs` counts of file extents per compression type, per image |

The tree: `d/text.txt` (compressible text, 20 bytes of it rewritten in place at offset 5000
after a sync, so the copy-on-write path ran over an existing compressed extent), `small.txt`
(1 980 compressible bytes, small enough for an inline extent), `mixed.bin` (alternating compressible and incompressible 16 KiB runs),
`sparse.bin` (1 MiB: a hole with no extent item, 62 KiB of data at 512 KiB, a trailing
hole) and `prealloc.bin` (a 64 KiB preallocated extent).

Made by `mkfs.btrfs` from btrfs-progs 6.6.3 (defaults: NO_HOLES, 16 KiB nodes, 4 KiB
sectors) and filled through guestfish (libguestfs) on Ubuntu under WSL2, whose appliance
ran the distribution kernel's btrfs driver. Every image passed `btrfs check --readonly`.
Captured 2026-10-19; compressed with `gzip -9 -n`, each expands to 134 217 728 bytes.
