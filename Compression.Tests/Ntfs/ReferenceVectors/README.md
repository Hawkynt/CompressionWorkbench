# NTFS WOF reference volume

`ntfs-wof.img.gz` is a 16 MiB NTFS volume holding Windows Overlay Filter ("system
compression") files, gzip-compressed (`gzip -9 -n`). It is made in two steps, both kept here
as documentation rather than build inputs:

1. `WofPayloadGenerator.cs` (an explicit test, run by hand on Windows) writes a 150 000-byte
   source file and its `WofCompressedData` streams: chunk table, then chunks. XPRESS4K,
   XPRESS8K and XPRESS16K chunks come from Windows' own encoder (`RtlCompressBuffer`,
   `COMPRESSION_FORMAT_XPRESS_HUFF`, ntdll). Windows offers no LZX encoder outside
   `compact.exe`, which needs an NTFS volume this could image only with administrator
   rights, so the LZX stream comes from this repository's encoder.
2. `generate.sh` formats the volume with `mkntfs` and, inside a libguestfs appliance, has
   ntfs-3g write each file the way WOF lays it out: the unnamed `$DATA` at the original
   length (written as zeros), the payload in the named stream `WofCompressedData`, and an
   `IO_REPARSE_TAG_WOF` reparse point with the file provider and the algorithm.

| File | Algorithm |
| --- | --- |
| `plain.bin` | none: the source itself |
| `xpress4k.bin`, `xpress8k.bin`, `xpress16k.bin` | XPRESS Huffman, 4 / 8 / 16 KiB chunks |
| `lzx.bin` | LZX, 32 KiB chunks |

`manifest.txt` is what the Linux kernel's ntfs3 driver (built with
`CONFIG_NTFS3_LZX_XPRESS`), mounted read-only in the same appliance, read back from every
file. All five match the source. A reader that ignores WOF gets the zeros of the unnamed
stream instead. Captured 2026-10-30 on Ubuntu under WSL2.
