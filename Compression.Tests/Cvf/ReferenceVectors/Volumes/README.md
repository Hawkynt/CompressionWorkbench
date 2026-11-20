# CVF volumes a real driver wrote

Each volume holds this repository's own data, as `MsDosRealDriverTests.Files()` generates it:

| File | Bytes | Content |
|------|------:|---------|
| `HELLO.TXT` | 33 | `Hello from CompressionWorkbench\r\n` |
| `RANDOM.BIN` | 5000 | `new Random(13).NextBytes` |
| `TEXT.TXT` | 20000 | `CvfSamples.Get("text-32k")[..20000]` |

The DOS volumes were made by the product's own tool, booted under `qemu-system-i386` from its install
disk 1 (the tool taken from the set's other disks) with a fresh 16 MB FAT16 hard disk as C: (33
cylinders, 16 heads, 63 sectors). The guest ran:

```
<TOOL> /CREATE C: /NEWDRIVE=E: /SIZE=0.5
COPY A:\IN\*.* E:\
<check> E:                 (no problems reported)
```

Each file was copied off the host disk unchanged except for one thing: bytes 4–511 of the final sector
(after the `MDR\0` signature) held stale memory unrelated to the volume and are zeroed here; the
volume with that slack zeroed still mounts and checks clean under the same DOS. Gzip-compressed for
check-in. The install media are not part of this repository.

| File | Product | Container | Clusters | Tool / check | Size, SHA-256 (uncompressed) |
|------|---------|-----------|----------|--------------|------------------------------|
| `msdos622-drvspace.001.gz` | **MS-DOS 6.22** (English, *Plus Enhanced Tools* 3.5" set) | `MSDBL6.0`, version 2 | JM-0-0 | `DRVSPACE` / `SCANDISK E: /CUSTOM` | 529 920 B, `3661700abe8de692bcedb58abd9aec2911b14e5103a93f0f900c8319193a929b` |
| `msdos60-dblspace.001.gz` | **MS-DOS 6.0** (English, *Plus Enhanced Tools* 3.5" set; `DBLSPACE.EX_` expanded by the set's own `EXPAND`) | `MSDSP6.0`, version 0 | DS-0-0 | `DBLSPACE` / `DBLSPACE /CHKDSK E:` | 529 920 B, `a48a46cb3612c39f5c077bd7b83279ea517db220d97892841f8d4722640081ba` |

The German MS-DOS 6.0 and 6.22 sets write volumes with the same system area; they are exercised by
the gated tests rather than checked in.

## DriveSpace 3 (Windows 98 SE)

Windows 98 SE (English CD), installed unattended under `qemu-system-i386` onto a 504 MB FAT16 disk with
*Disk compression tools* selected, with a second, empty 64 MB FAT16 disk as D:. In its DriveSpace 3:

1. `DRVSPACE /CREATE D: /SIZE=3` with *Standard compression* set, then `COPY A:\*.* G:\` from a floppy
   holding the three files → `DRVSPACE.002`;
2. *Settings* → *HiPack compression*, *Create Empty* (3 MB on D:), copy → `DRVSPACE.003`;
3. *Create Empty* again (3 MB, HiPack still set), copy, then Compression Agent on that drive with
   *UltraPack all files* → `DRVSPACE.004`.

Windows was then shut down and the files copied off D: unchanged (their final sectors hold nothing after
`MDR\0`). Each mounts under Windows 98's real-mode `DRVSPACE.BIN`, reads byte-exact and passes
`SCANDISK C: /CHECKONLY`.

| File | Setting | `TEXT.TXT` cluster | Size, SHA-256 (uncompressed) |
|------|---------|--------------------|------------------------------|
| `win98-drvspace3-normal.002.gz` | Standard compression | JM-0-0, 21 sectors | 3 145 728 B, `43bd23896701bd5aee55c591ef8d84214089d40ad10279f9fe479f04f8a3ac2a` |
| `win98-drvspace3-hipack.003.gz` | HiPack | JM-0-1, 17 sectors | 3 145 728 B, `11a774ba76669a1b3374819842526dd70141362b44b9e0a3bfa95411104fd42d` |
| `win98-drvspace3-ultrapack.004.gz` | UltraPack (Compression Agent) | SQ-0-0, 13 sectors | 3 145 728 B, `868afa1f60c51c8c2d253e54a6ca81278524f875645226ea225892aff23c2d0c` |

`HELLO.TXT` and `RANDOM.BIN` are stored runs (1 and 10 sectors) in all three.
