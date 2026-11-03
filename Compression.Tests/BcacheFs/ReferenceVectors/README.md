# bcachefs reference images

Volumes written by bcachefs-tools, checked in so the reader tests run without
Linux tools. `generate.sh` documents how they were made; it is not a build input.

| File | Made by | What it is |
|------|---------|------------|
| `bcachefs-tools-1.3-format.img.gz` | `bcachefs format --bucket=32k` from Ubuntu's bcachefs-tools (reports `1.3.3`), 32 MiB device | An empty volume at metadata version 1.3: root directory, `lost+found`, cleanly shut down. |
| `bcachefs-tools-1.39-format-source.img.gz` | `bcachefs format --force --bucket_size=32k --source=<tree>` from bcachefs-tools v1.39.6 built from the release tarball, 32 MiB device | The tree in `manifest.txt` copied in by the tool's own write path: nested directories, an empty directory, a file spanning many extents, a file whose length is not a whole block, a sparse file, and two symbolic links; files owned by uid 1234 / gid 4321 and 0 / 100, permission bits 0640 and 0755, a modification time with nanoseconds and one before the Unix epoch. Formatted as root so the tool could read files other users own. Metadata version 1.39. |
| `bcachefs-tools-1.39-lz4-crc32c.img.gz` | `bcachefs format --force --bucket_size=32k --compression=lz4 --data_checksum=crc32c --source=<tree>`, v1.39.6, as root, 32 MiB device | The tree in `manifest-compressed.txt`, LZ4-compressed, every extent crc32c-checksummed. |
| `bcachefs-tools-1.39-gzip-crc64.img.gz` | the same with `--compression=gzip --data_checksum=crc64` | The same tree, raw-DEFLATE ("gzip") extents, crc64 checksums. |
| `bcachefs-tools-1.39-zstd-xxhash.img.gz` | the same with `--compression=zstd --data_checksum=xxhash` | The same tree, zstd extents, xxhash checksums. |
| `linux-6.8-zstd.img.gz` | `bcachefs format -q --bucket=32k` (Ubuntu, 1.3.3), then the tree copied in by the Ubuntu 24.04 kernel 6.8.0-146 through the libguestfs appliance, mounted with `compression=zstd` | The same tree written by the kernel's own write path: zstd extents, crc32c checksums, inline data for small files. The kernel upgraded it to metadata version 1.4. |
| `manifest-compressed.txt` | `generate-compressed.sh` | SHA-256, length and path of every file in the compressed tree: text spanning many extents, a repeating binary pattern, incompressible noise (stored plain beside the rest), an odd-sized file, a file smaller than a block, and a sparse file. |
| `manifest.txt` | `generate.sh` | SHA-256, length and path of every file in the tree; `L` lines are links and their targets, `D` lines directories, `M` lines a file's permission bits (octal), owner, group, and modification and change times in nanoseconds since the epoch. |

Every image passed `bcachefs fsck -n` from the tools that wrote it; the kernel
image passed both Ubuntu's 1.3.4 and v1.39.6. `generate-compressed.sh` makes the
compressed set (`KERNEL_IMAGES=1` adds the kernel one); captured 2026-10-19.

Captured 2026-10-01 on Ubuntu under WSL2 (kernel 6.6, which has no bcachefs
driver, so the userspace tools were the only writers). Compressed with
`gzip -9 -n`; each expands to 33 554 432 bytes.
