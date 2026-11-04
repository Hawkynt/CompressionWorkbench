# PAR2 reference vectors

Written on 2026-10-30 in WSL (Ubuntu 24.04) by par2cmdline 0.8.1:

| File | Origin |
| --- | --- |
| `a.txt` | `hello par2 recovery set\n` × 40 (960 bytes). |
| `b.bin` | 3000 bytes, byte i = `i*31+7`. |
| `set.par2`, `set.vol0+4.par2` | `par2 create -s512 -c4 -n1 set.par2 a.txt b.bin`: 512-byte slices, 8 source slices, 4 recovery slices in one volume file. |

Our writer, given the same two files, slice size and recovery count, must produce the same recovery set
id and the same packets (main, file description, slice checksums, recovery slices) as these files; only
the creator packet differs, because it names the program.
