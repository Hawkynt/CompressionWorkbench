# MS-DOS Boot-Image Staging for DBLSPACE / DRVSPACE Validation

This document describes how to stage a legal MS-DOS boot image for the
DOSBox-X validation gates in `Compression.Tests/ExternalFsInteropTests.cs`.

**This repo does not auto-download MS-DOS.** Microsoft has not released
MS-DOS 6.x to the public domain. The Internet Archive hosts install media
(<https://archive.org/details/msdos622>) but distribution rights are
unsettled, so the staging is opt-in via environment variables. The tests
skip cleanly with actionable hints when no image is staged.

For FAT validation (no proprietary binaries needed) we ship FreeDOS — see
`FreeDosCache.cs` and the `Fat_OurImage_FreedosChkdsk` test.

---

## Two-gate split: DBLSPACE vs DRVSPACE

DBLSPACE and DRVSPACE are **on-disk-different products**:

| Product   | DOS version | Driver        | CVF magic | Env var                       |
| --------- | ----------- | ------------- | --------- | ----------------------------- |
| DoubleSpace | 6.0 / 6.2 | `DBLSPACE.BIN` | `DBLS`    | `CWB_MSDOS_DBLSPACE_BOOT_IMG` |
| DriveSpace  | 6.22+ / Win95 OSR2 | `DRVSPACE.BIN` | `DVRS`    | `CWB_MSDOS_DRVSPACE_BOOT_IMG` |

Each test builds its CVF via `DoubleSpaceWriter` with the matching
`Variant` (`CvfVariant.DoubleSpace60` for DBLS, `CvfVariant.DriveSpace62`
or `CvfVariant.DriveSpace30` for DVRS) and runs the matching `/CHKDSK`
command inside DOSBox-X.

A legacy `CWB_MSDOS_BOOT_IMG` env var is honored as a fallback for
either gate when the variant-specific var is not set.

## Building a DBLSPACE boot image (MS-DOS 6.0 / 6.2)

1. Acquire MS-DOS 6.0 or 6.2 install floppies from a legal source you
   own. Common legal sources:
   - An original boxed MS-DOS license you purchased.
   - An MSDN / TechNet subscription that included DOS images.
   - The Internet Archive (`https://archive.org/details/msdos622` for
     6.22; 6.0 is harder to find publicly). Not formally licensed; use
     at your own risk.
2. Build a fresh hard-disk image in DOSBox-X:
   ```
   imgmake msdos60.img -t hd_st251 -fs fat -bat
   ```
3. Boot the install floppy and run `SETUP` to install MS-DOS 6.0/6.2 to
   the new C:.
4. After install, ensure `C:\DOS\DBLSPACE.BIN`, `C:\DOS\DBLSPACE.EXE`
   and `C:\DOS\DBLSPACE.SYS` are present.
5. Set the env var:
   ```
   set CWB_MSDOS_DBLSPACE_BOOT_IMG=C:\path\to\msdos60.img
   ```
6. Run `dotnet test --filter DoubleSpace_OurImage_DblspaceChkdsk`.

## Building a DRVSPACE boot image (MS-DOS 6.22+)

1. Acquire MS-DOS 6.22 install floppies from a legal source you own.
2. Same `imgmake` recipe as above, but install MS-DOS 6.22 to the new
   C:.
3. After install, run `DRVSPACE` once to convert the C: drive (or just
   ensure `C:\DOS\DRVSPACE.BIN`, `C:\DOS\DRVSPACE.EXE` and
   `C:\DOS\DRVSPACE.SYS` are present — the test only needs the driver
   files, not a converted drive).
4. Set the env var:
   ```
   set CWB_MSDOS_DRVSPACE_BOOT_IMG=C:\path\to\msdos622.img
   ```
5. Run `dotnet test --filter DriveSpace_OurImage_DrvspaceChkdsk`.

## DOSBox-X prerequisite

Both gates require DOSBox-X (not classic DOSBox). Plain DOSBox does not
load `DBLSPACE.BIN` / `DRVSPACE.BIN` reliably.

- Windows: <https://dosbox-x.com/> (portable .zip is fine)
- Linux / WSL: `sudo apt install -y dosbox-x`

The harness probes both Windows PATH and WSL — see `DosboxRunner.cs`.
The test surfaces sentinel exit codes (`ExitCode_DosboxMissing`,
`ExitCode_TimedOut`) so a missing or wedged DOSBox-X skips cleanly with
an actionable diagnostic instead of failing.

## What the gates assert

```
DBLSPACE.EXE /CHKDSK D: > E:\OUTPUT.TXT     (DBLSPACE gate)
DRVSPACE.EXE /CHKDSK D: > E:\OUTPUT.TXT     (DRVSPACE gate)
```

The host temp dir is mounted as DOSBox-X drive `E:` so the autoexec
script can drop a host-readable transcript. The test reads `OUTPUT.TXT`
from the host side and asserts it contains neither `error` nor
`invalid` (case-insensitive). An empty `OUTPUT.TXT` is treated as
`Assert.Ignore` — the harness ran but the boot image lacked the driver.

## FreeDOS as fallback (FAT only, no DBLSPACE/DRVSPACE)

For FAT-image validation we ship `FreeDosCache.cs`, which downloads the
GPL-licensed FreeDOS 1.4 LiveCD ISO from
<https://download.freedos.org/1.4/FD14-LiveCD.zip> (SHA-256
`2020ff6bb681967fd6eff8f51ad2e5cd5ab4421165948cef4246e4f7fcaf6339`,
hash-pinned). The `Fat_OurImage_FreedosChkdsk` gate uses this to run
`chkdsk` against a `FatWriter` image without needing any proprietary
binaries.

FreeDOS does **not** ship `DBLSPACE.BIN` or `DRVSPACE.BIN`, so it
cannot validate CVF images. DBLSPACE / DRVSPACE remain MS-DOS-only
gates.

## Why this is split

The user explicitly asked for separate test gates because the two
products have different drivers, different magic bytes, and different
CVF version codes. Conflating them under one test would hide which
variant actually validates against which driver — and we already
maintain three `CvfVariant` enum values inside `DoubleSpaceWriter`, so
splitting the harness brings the test surface in line with the writer
surface.
