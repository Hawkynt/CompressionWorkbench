# Rock Ridge reference images

ISO images xorriso wrote, gzip-compressed (`gzip -9 -n`). `generate.sh` documents how they
were made; it is not a build input.

| File | What it is |
|------|------------|
| `rr.iso.gz` | Rock Ridge, no Joliet; `big.txt` and `zeros.bin` zisofs-compressed (`-set_filter --zisofs`) |
| `rr-joliet.iso.gz` | the same with a Joliet tree, so zisofs is only flagged in the primary tree's Rock Ridge records |
| `rr-moved.iso.gz` | the same tree through `xorriso -as mkisofs -R` (mkisofs emulation and its deep-directory rules) |
| `manifest-*.txt` | what the Linux kernel's isofs driver lists and reads back from each image: `<type> <path>\|<link target>` lines, then `<sha256> <path>` per file |

The tree holds a 214-character name (too long for one directory record, so NM continues
through a CE continuation area), four
symbolic links (relative, absolute, upward, dangling), a directory nested eleven levels
deep, compressible text and zeros (zisofs) and incompressible bytes.

Made by xorriso and read through guestfish (libguestfs), whose appliance ran the
distribution kernel's isofs driver, on Ubuntu under WSL2; captured 2026-10-23.
