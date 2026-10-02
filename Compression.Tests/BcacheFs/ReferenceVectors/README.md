# bcachefs reference images

Volumes written by bcachefs-tools, checked in so the reader tests run without
Linux tools. `generate.sh` documents how they were made; it is not a build input.

| File | Made by | What it is |
|------|---------|------------|
| `bcachefs-tools-1.3-format.img.gz` | `bcachefs format --bucket=32k` from Ubuntu's bcachefs-tools (reports `1.3.3`), 32 MiB device | An empty volume at metadata version 1.3: root directory, `lost+found`, cleanly shut down. |
| `bcachefs-tools-1.39-format-source.img.gz` | `bcachefs format --force --bucket_size=32k --source=<tree>` from bcachefs-tools v1.39.6 built from the release tarball, 32 MiB device | The tree in `manifest.txt` copied in by the tool's own write path: nested directories, an empty directory, a file spanning many extents, a file whose length is not a whole block, a sparse file, and two symbolic links; files owned by uid 1234 / gid 4321 and 0 / 100, permission bits 0640 and 0755, a modification time with nanoseconds and one before the Unix epoch. Formatted as root so the tool could read files other users own. Metadata version 1.39. |
| `manifest.txt` | `generate.sh` | SHA-256, length and path of every file in the tree; `L` lines are links and their targets, `D` lines directories, `M` lines a file's permission bits (octal), owner, group, and modification and change times in nanoseconds since the epoch. |

Both images passed `bcachefs fsck -n` from the tools that wrote them.

Captured 2026-10-01 on Ubuntu under WSL2 (kernel 6.6, which has no bcachefs
driver, so the userspace tools were the only writers). Compressed with
`gzip -9 -n`; each expands to 33 554 432 bytes.
