# CVF volumes a real driver wrote

## `msdos622-drvspace.001.gz`

A DriveSpace compressed volume file (`MSDBL6.0`, version 2, JM-0-0 clusters) created and filled by
**MS-DOS 6.22** (English, *Plus Enhanced Tools* 3.5" set), booted under `qemu-system-i386` from its own
install disk 1 with `DRVSPACE.EXE` from disk 2. The host was a fresh 16 MB FAT16 hard disk (33
cylinders, 16 heads, 63 sectors). The guest ran:

```
DRVSPACE /CREATE C: /NEWDRIVE=E: /SIZE=0.5
COPY A:\IN\*.* E:\
SCANDISK E: /CUSTOM        (reported no problems)
```

The files are this repository's own data, as `MsDosRealDriverTests.Files()` generates them:

| File | Bytes | Content |
|------|------:|---------|
| `HELLO.TXT` | 33 | `Hello from CompressionWorkbench\r\n` |
| `RANDOM.BIN` | 5000 | `new Random(13).NextBytes` |
| `TEXT.TXT` | 20000 | `CvfSamples.Get("text-32k")[..20000]` |

`C:\DRVSPACE.001` was copied off the host disk unchanged except for one thing: bytes 4–511 of the
final sector (after the `MDR\0` signature) held stale memory unrelated to the volume and are zeroed
here. The volume with that slack zeroed still mounts and checks clean under the same MS-DOS 6.22.
Uncompressed size 529 920 bytes (1035 sectors), SHA-256
`3661700abe8de692bcedb58abd9aec2911b14e5103a93f0f900c8319193a929b`; gzip-compressed for check-in.

The DOS install media are not part of this repository.
