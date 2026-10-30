# RAR 4 reference vectors

Archives in the RAR 4 format (compression version 29) that real RAR encoders wrote. Every
file in them carries its CRC-32, so a reader that gets one bit wrong fails the check.

## `rar624-*.rar`

Written on 2026-10-26 by RAR 6.24 for Linux x64 (`rarlinux-x64-624.tar.gz` from rarlab.com,
trial version), the last RAR release that still writes RAR 4 archives. The inputs come from
`make_inputs.py` (deterministic, fixed seed). The script and the commands are below.
`unrar t` (UNRAR 7.00) tests every archive OK.

```sh
A="a -ma4 -ep -idq -ts-"
rar $A -m3 -mct- -mcE- -mcD- -mcA- -mcC- -mcI- lz.rar text.txt
rar $A -m5 -mct- -md4m best.rar text.txt table.bin
rar $A -s -m3 -mct- solid.rar text.txt x86.bin table.bin
rar $A -m3 -mct- -mcE+ e8.rar x86.bin
rar $A -m3 -mct- -mcD+ delta.rar table.bin
rar $A -m3 -mct- -mcA+ audio.rar audio.wav
rar $A -m3 -mct- -mcC+ rgb.rar image.bmp
rar $A -m3 -mct- -mcI+ itanium.rar ia64.bin
rar $A -m3 -mct+ ppmd.rar text.txt
```

## `libarchive-*.rar`

From libarchive's test suite (`libarchive/test/`, tag v3.7.7, BSD-2-Clause), decoded from
their `.uu` form:

- `test_read_format_rar_filter.rar` becomes `libarchive-filter.rar`.
- `test_read_format_rar_compress_best.rar` becomes `libarchive-compress-best.rar`.
- `test_read_format_rar_compress_normal.rar` becomes `libarchive-compress-normal.rar`.
