# Stacker STACVOL on-disk format

> **Verification status.**
> - **Every volume `StackerFormatDescriptor.Create` writes is mounted by the independent `dmsdos`
>   driver** ("stacker version 3 CVF", or "stacker version 4 CVF" for `Version` 4) and read back byte-exact — for every published codec, and
>   again after an add and a remove (`CvfCreateDmsdosTests`, `GenuineStackerDmsdosTests`,
>   `GenuineStackerCodecOracleTests`). Our reader reads the same volumes byte-exact.
> - **Cluster codecs:** SD-3, SD-4 and DS are specified in
>   [`docs/CVF-CODECS-ON-DISK.md`](../../../docs/CVF-CODECS-ON-DISK.md), together with the AMAP
>   flags and the fragmented-cluster layout. The reader decodes the full SD-4 format, pinned to
>   clusters the dmsdos compressors wrote (`CvfReferenceVectorTests`); the writer's SD-4 output uses
>   back-references and run-coded tables. Fragmented clusters are read; suballocated clusters are
>   refused, not guessed at.
> - **One layout only.** The writer produces nothing but the STACVOL described here; edits
>   (add, remove, defragment, wipe, shrink) rebuild the volume and keep its label.

A Stacker compressed volume is an ordinary MS-DOS host file (canonically `STACVOL.DSK`, also
`*.STA`/`*.STK`) that wraps a compressed inner FAT12 volume. Everything is little-endian, sector size
512, cluster size 8 KiB (16 sectors).

## Layout

| Where | What |
|---|---|
| Sector 0 | `"STACKER"` magic, a minimal FAT BPB, raw `0x0A` `0x1A` at 0x4E/0x4F, and the **obfuscated superblock** at 0x50 (0x30 bytes, rolling-XOR cipher seeded at 0x4C: `tk = ROL1(0xC4 - key); enc = tk ^ plain; key = enc`) |
| Superblock (decoded) | version @0x60 (300 = Stacker 3, 410 = Stacker 4; the writer stamps exactly the one asked for), sector size @0x62, total sectors @0x6C, emulated boot block @0x70, AMAP start @0x74, FAT start @0x76, data start @0x7A |
| Emulated boot block | the inner volume's BPB |
| FAT + AMAP | interleaved from the FAT start: the AMAP sector for cluster *c* is `(area/6)*9 + area%6 + 3 + fatStart` with `area = c*3/512` (FAT12) |
| AMAP entry (3 bytes) | absolute physical sector (16 bits), then a byte holding the run length in sectors − 1 (low nibble) and the cluster kind (bits 7..5, see the spec) |

A compressed run can never fill all 16 sectors of its cluster — such a run reads as stored — so a
cluster whose payload would need them all is stored raw.

**Confirmed against the dmsdos documentation**
(<https://github.com/sandsmark/dmsdos/tree/master/doc>, `dmsdos.doc`): Stacker uses an 8 KiB cluster
and two compressed-cluster codecs keyed by a header — `SD-3` (Stacker 3) and `0x81-0` (Stacker 4).
