# Microsoft DOS images for the DoubleSpace / DriveSpace / DriveSpace 3 driver gates

`Compression.Tests/Cvf/MsDosRealDriverTests.cs` and `DriveSpace3RealDriverTests.cs` boot real Microsoft
DOS under `qemu-system-i386` and let Microsoft's own compression drivers mount, read, write and check
the volumes our writers create. The harness is `MsDosGuest.cs` with the script `MsDos/msdos-guest.sh`.

**This repository neither ships nor downloads any of it.** The gates are opt-in: point the environment
variables below at media you own. Every test skips cleanly with an actionable message when its media
or a tool is missing. Never commit the images.

For FAT validation without proprietary binaries see `FreeDosCache.cs` and `Fat_OurImage_FreedosChkdsk`
(FreeDOS ships no DBLSPACE / DRVSPACE driver, so it cannot check a compressed volume).

## Variables

| Variable | Points at | Driver / checker |
| -------- | --------- | ---------------- |
| `CWB_MSDOS_DBLSPACE_BOOT_IMG` | MS-DOS 6.0 (English) install disk 1; the other disks beside it (`DBLSPACE.EX_` is on one of them and is expanded in the guest by the set's own `EXPAND`) | `DBLSPACE.BIN`, `DBLSPACE /CHKDSK` |
| `CWB_MSDOS_DBLSPACE_DE_BOOT_IMG` | MS-DOS 6.0 (German) install disk 1, the other disks beside it | as above |
| `CWB_MSDOS_DRVSPACE_BOOT_IMG` (legacy `CWB_MSDOS622_BOOT_IMG`) | MS-DOS 6.22 (English) install disk 1; `DRVSPACE.EXE` is taken from the disk beside it that holds it | `DRVSPACE.BIN`, `SCANDISK /CUSTOM` |
| `CWB_MSDOS_DRVSPACE_DE_BOOT_IMG` | MS-DOS 6.22 (German) install disk 1, the other disks beside it | as above |
| `CWB_MSDOS71_BOOT_IMG` | a bootable MS-DOS 7.1 floppy image (the 7.1 installer's boot disk serves) | — |
| `CWB_MSDOS71_DRVSPACE_BIN` | that distribution's `DRVSPACE.BIN` (inside its package archives; extract it with the distribution's own unpacker) | `DRVSPACE.BIN` |
| `CWB_WIN98_ISO` | the Windows 98 SE CD image; its El Torito boot floppy holds the real-mode `DRVSPACE.BIN`, and `SCANDISK.EXE` inside its `EBD.CAB` | `DRVSPACE.BIN`, `SCANDISK /CHECKONLY` |

## Tools

Linux or WSL with `qemu-system-i386` (`qemu-system-x86`), `mtools`, `sfdisk` (`fdisk`) and `python3`;
for `CWB_WIN98_ISO` also `geteltorito` (`genisoimage`) and `cabextract`. On Windows the harness runs
inside the default WSL distro.

## What the harness does

1. Copies the boot floppy, deletes installer-only files to make room, and adds `CONFIG.SYS`, the
   product's INI (`DBLSPACE.INI` / `DRVSPACE.INI`: removable-drive and drive-letter settings; under 7.1
   and Windows 98 also `ActivateDrive=E,C0`, so the driver mounts `C:\DRVSPACE.000` as C: at boot),
   `SCANDISK.INI` (every problem class set to `Fix`, so SCANDISK runs unattended and its exit code
   reports whatever it found) and the test's `AUTOEXEC.BAT`. Under 7.1 `MSDOS.SYS` gets `DrvSpace=1`;
   Windows 98's floppy keeps its own, empty `MSDOS.SYS` (its `IO.SYS` loads `DRVSPACE.BIN` by default
   and stops at *Starting Windows 98...* once `MSDOS.SYS` has an `[Options]` section) and loads
   `HIMEM.SYS`, without which its SCANDISK exits at once with code 2.
2. Builds a fresh FAT16 hard disk (default 33 cylinders × 16 heads × 63 sectors, 16 MB) holding the
   volume under test as `C:\DBLSPACE.000` or `C:\DRVSPACE.000` (hidden, system, read-only).
3. Boots the floppy with the disk as the primary IDE drive (on the Windows 98 floppy answering its
   *Boot from CD-ROM* menu), waits for the batch to append `DONE` to `A:\LOG.TXT`, captures the text
   screen through the qemu monitor (an interactive prompt shows up there), and copies out `LOG.TXT`,
   `A:\OUT\*` and the volume files the guest left.

MS-DOS 6.x refuses to mount or create a compressed volume on a removable drive, so the host has to be
a hard disk. MS-DOS 6.22's `DRVSPACE` has no `/CHKDSK`; `SCANDISK` is its checker.

## What the gates establish

| Test | Driver | Volumes | Asserted |
| ---- | ------ | ------- | -------- |
| `DoubleSpace_OurVolume_MsDos60MountsReadsWritesAndChecksClean` | MS-DOS 6.0 `DBLSPACE.BIN`, English and German | DoubleSpace descriptor, DS and stored | mounts; every file byte-exact; a file the driver writes reads back (through DOS and through our reader); `DBLSPACE /CHKDSK` finds no errors |
| `DriveSpace_OurVolume_MsDos622MountsReadsWritesAndChecksClean` | MS-DOS 6.22 `DRVSPACE.BIN`, English and German | DriveSpace descriptor, JM and stored | as above; `SCANDISK` clean |
| `DoubleSpaceAndDriveSpace_OurVolume_MsDos71MountsAndReads` | MS-DOS 7.1 `DRVSPACE.BIN` | DoubleSpace DS and stored, DriveSpace JM, DriveSpace 3 JM and SQ | mounts at boot; every file byte-exact |
| `DriveSpace3_OurVolume_Win98MountsReadsAndChecksClean` | Windows 98 SE `DRVSPACE.BIN` | DriveSpace 3 descriptor, stored / JM / SQ / Auto | mounts at boot; every file byte-exact; `SCANDISK /CHECKONLY` clean |
| `DoubleSpace_MsDos60CreatedVolume_OurReaderReads`, `DriveSpace_MsDos622CreatedVolume_OurReaderReads` | `DBLSPACE /CREATE`, `DRVSPACE /CREATE`, English and German | the driver's own volume | our reader reads every file byte-exact |

The checked-in volumes in `Cvf/ReferenceVectors/Volumes/` (provenance in that folder's README) come
from these drivers — the DriveSpace 3 ones from Windows 98's DriveSpace 3 and Compression Agent, which
need the Windows GUI and are not driven by the gates; the always-on tests compare our writers' system
areas against them.

Not covered, by measurement rather than omission: MS-DOS 6.22 refuses a DoubleSpace volume ("in
DoubleSpace format, which is incompatible with DriveSpace") and MS-DOS 6.0 a DriveSpace one ("not a
valid compressed volume file"), so each product is gated under its own DOS and under 7.1.
