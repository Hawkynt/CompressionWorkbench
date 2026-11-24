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
| `linux-6.8-reflink.img.gz` | this package's writer (128 MiB, `orig.bin` of 300 000 bytes and `small.txt`), then through the Ubuntu 24.04 kernel 6.8.0-146 in the libguestfs appliance: `cp --reflink=always orig.bin clone.bin` and `clone2.bin`, `dd if=/dev/zero of=clone2.bin bs=4096 seek=10 count=2 conv=notrunc`, `seq 1 30000 > kseq.txt`, `cp --reflink=always kseq.txt kseq-clone.txt` | Reflinked data: the kernel moved both files' data into the reflink tree (`reflink_v` with refcounts 2 to 4) and left `reflink_p` keys behind, the partly overwritten clone split around a plain extent with padded halves. `bcachefs fsck -n` (1.3.4) passed it; captured 2026-11-24. |
| `manifest-reflink.txt` | `sha256sum` inside the same kernel mount | SHA-256, length and path of every file in the reflink volume. |
| `bcachefs-tools-1.39-encrypted-key.img.gz` | `bcachefs format --force --encrypted --bucket_size=32k`, v1.39.6, as root, passphrase `Secr3t` (three times on standard input) | Only the superblock: under WSL the tools wrap the master key with the scrypt-derived passphrase key and then fail to open the new volume through the kernel keyring, so the volume is never initialised. It proves the key wrapping; files on passphrase volumes are proven on our own volumes, which `bcachefs fsck` unlocks from the passphrase. |
| `linux-6.8-encrypted.img.gz` | `bcachefs format -q --encrypted --no_passphrase --bucket=32k` (Ubuntu, 1.3.x), then the tree copied in by the 6.8 kernel through the libguestfs appliance | The kernel's write path: plain extents with 80-bit MACs, inline data, the key stored in the clear (the appliance cannot be handed a passphrase). |
| `linux-6.8-encrypted-zstd-widemacs.img.gz` | as above with `--wide_macs`, mounted `compression=zstd` | zstd-compressed encrypted extents (the compression type and size are part of their nonce) with full 128-bit MACs. |
| `manifest-encrypted.txt` | `generate-encrypted.sh` | SHA-256, length and path of every file in the encrypted tree (a pattern spanning several extents, an odd-sized text, a tiny file, a sparse file); the tree also holds the link `link -> text/odd-size.txt`. |
| `manifest-compressed.txt` | `generate-compressed.sh` | SHA-256, length and path of every file in the compressed tree: text spanning many extents, a repeating binary pattern, incompressible noise (stored plain beside the rest), an odd-sized file, a file smaller than a block, and a sparse file. |
| `manifest.txt` | `generate.sh` | SHA-256, length and path of every file in the tree; `L` lines are links and their targets, `D` lines directories, `M` lines a file's permission bits (octal), owner, group, and modification and change times in nanoseconds since the epoch. |

Every image passed `bcachefs fsck -n` from the tools that wrote it; the kernel
image passed both Ubuntu's 1.3.4 and v1.39.6. `generate-compressed.sh` makes the
compressed set (`KERNEL_IMAGES=1` adds the kernel one); captured 2026-10-19.
`generate-encrypted.sh` makes the encrypted set; every kernel image passed `bcachefs fsck
-n` of the tools that wrote it (the passphrase given on standard input);
captured 2026-11-24.

Captured 2026-10-01 on Ubuntu under WSL2 (kernel 6.6, which has no bcachefs
driver, so the userspace tools were the only writers). Compressed with
`gzip -9 -n`; each expands to 33 554 432 bytes.
