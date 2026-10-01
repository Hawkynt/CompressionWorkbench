# bcachefs reference images

Volumes written by bcachefs-tools, checked in so the reader tests run without
Linux tools. `generate.sh` documents how they were made; it is not a build input.

| File | Made by | What it is |
|------|---------|------------|
| `bcachefs-tools-1.3-format.img.gz` | `bcachefs format --bucket=32k` from Ubuntu's bcachefs-tools (reports `1.3.3`), 32 MiB device | An empty volume at metadata version 1.3: root directory, `lost+found`, cleanly shut down. |
| `bcachefs-tools-1.39-format-source.img.gz` | `bcachefs format --force --bucket_size=32k --source=<tree>` from bcachefs-tools v1.39.6 built from the release tarball, 32 MiB device | The tree in `manifest.txt` copied in by the tool's own write path: nested directories, an empty directory, a file spanning many extents, a file whose length is not a whole block, a sparse file, and two symbolic links. Metadata version 1.39. |
| `manifest.txt` | `generate.sh` | SHA-256, length and path of every file in the tree; `L` lines are links and their targets, `D` lines directories. |

Both images passed `bcachefs fsck -n` from the tools that wrote them.

Captured 2026-10-01 on Ubuntu under WSL2 (kernel 6.6, which has no bcachefs
driver, so the userspace tools were the only writers). Compressed with
`gzip -9 -n`; each expands to 33 554 432 bytes.
