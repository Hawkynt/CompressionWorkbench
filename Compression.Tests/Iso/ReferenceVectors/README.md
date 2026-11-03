# ISO 9660 reference images

## Interleaved files

| File | What it is |
|------|------------|
| `interleave-1-1.iso.gz` | `DATA.BIN` (10940 random bytes) recorded interleaved, File Unit Size 1 and Interleave Gap Size 1, in the primary and the Joliet tree |
| `interleave-2-3.iso.gz` | the same file with File Unit Size 2 and Interleave Gap Size 3, primary tree only |
| `manifest-interleave.txt` | per image: unit, gap, the SHA-256 of the file genisoimage was given and of what Windows read back |

No mastering tool writes interleaved files, so `mkinterleaved.py` makes them from genisoimage
images: the file's sectors are spread into units with filler (0xA5) gaps after the volume, the
directory records are patched, and the original contiguous copy is overwritten (0x5A), so a reader
that ignores the interleave gets the wrong bytes. Windows 11 mounted each image with its own CDFS
driver (`Mount-DiskImage`) and read `DATA.BIN` back intact; captured 2026-11-03. The Linux isofs
driver and 7-Zip do not implement interleaving (isofs reports such files as empty).

## Rock Ridge

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
