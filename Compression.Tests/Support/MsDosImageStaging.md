# MS-DOS images for the DoubleSpace / DriveSpace driver gates

`Compression.Tests/Cvf/MsDosRealDriverTests.cs` boots real MS-DOS under `qemu-system-i386` and lets
Microsoft's own compression drivers mount, read, write and check the volumes our writers create.
The harness is `MsDosGuest.cs`.

**This repository neither ships nor downloads MS-DOS.** The gates are opt-in: point the environment
variables below at install media you own. Every test skips cleanly with an actionable message when
an image or a tool is missing. Never commit the images.

For FAT validation without proprietary binaries see `FreeDosCache.cs` and `Fat_OurImage_FreedosChkdsk`
(FreeDOS ships no DBLSPACE / DRVSPACE driver, so it cannot check a compressed volume).

## Variables

| Variable | Points at | Used for |
| -------- | --------- | -------- |
| `CWB_MSDOS_DRVSPACE_BOOT_IMG` (legacy `CWB_MSDOS622_BOOT_IMG`) | MS-DOS 6.22 install disk 1 (`Disk1.img`, 1.44 MB). `Disk2.img` must sit beside it — it provides `DRVSPACE.EXE`. | DriveSpace gates (mount, read, write, `SCANDISK`, `DRVSPACE /CREATE`) |
| `CWB_MSDOS71_BOOT_IMG` | A bootable MS-DOS 7.1 floppy image (the 7.1 installer's boot disk serves) | DoubleSpace and DriveSpace read gates |
| `CWB_MSDOS71_DRVSPACE_BIN` | That distribution's `DRVSPACE.BIN` | as above |

The 6.22 set is the stock three-disk *MS-DOS 6.22 Plus Enhanced Tools* 3.5" set; disk 1 boots and
already holds `DRVSPACE.BIN` and `SCANDISK.EXE`. The 7.1 `DRVSPACE.BIN` sits inside the
distribution's package archives; extract it with the distribution's own unpacker.

## Tools

Linux or WSL with `qemu-system-i386` (`qemu-system-x86`), `mtools`, `sfdisk` (`fdisk`) and `python3`.
On Windows the harness runs inside the default WSL distro.

## What the harness does

1. Copies the boot floppy, deletes installer-only files to make room, and adds `CONFIG.SYS`,
   `DRVSPACE.INI` (removable-drive and drive-letter settings; under 7.1 also `ActivateDrive=E,C0`, so
   the driver mounts `C:\DRVSPACE.000` as C: at boot), `SCANDISK.INI` (every problem class set to
   `Fix`, so SCANDISK runs unattended and its exit code reports whatever it found) and the test's
   `AUTOEXEC.BAT`; under 7.1 `MSDOS.SYS` gets `DrvSpace=1`, `DblSpace=1`, `BootGUI=0`.
2. Builds a fresh FAT16 hard disk (default 33 cylinders × 16 heads × 63 sectors, 16 MB) holding the
   volume under test as `C:\DRVSPACE.000` (hidden, system, read-only).
3. Boots the floppy with the disk as the primary IDE drive, waits for the batch to append `DONE` to
   `A:\LOG.TXT`, captures the text screen through the qemu monitor (an interactive prompt shows up
   there), and copies out `LOG.TXT`, `A:\OUT\*` and the `DRVSPACE.00x` files the guest left.

MS-DOS 6.22 refuses to mount or create a compressed volume on a removable drive, so the host has to
be a hard disk. MS-DOS 6.22's `DRVSPACE` has no `/CHKDSK`; `SCANDISK` is its checker.

## What the gates establish

| Test | Driver | Volumes | Asserted |
| ---- | ------ | ------- | -------- |
| `DriveSpace_OurVolume_MsDos622MountsReadsWritesAndChecksClean` | MS-DOS 6.22 `DRVSPACE.BIN` | DriveSpace descriptor, JM and stored | mounts; every file byte-exact; a file the driver writes reads back (through DOS and through our reader); `SCANDISK` clean |
| `DoubleSpaceAndDriveSpace_OurVolume_MsDos71MountsAndReads` | MS-DOS 7.1 `DRVSPACE.BIN` | DoubleSpace DS and stored, DriveSpace JM | mounts at boot; every file byte-exact |
| `DriveSpace_MsDos622CreatedVolume_OurReaderReads` | MS-DOS 6.22 `DRVSPACE /CREATE` | the driver's own volume | our reader reads every file byte-exact |

The checked-in volume `Cvf/ReferenceVectors/Volumes/msdos622-drvspace.001.gz` is the output of the
last gate (provenance in that folder's README); the always-on tests compare our writer's system area
against it.

Not covered, by measurement rather than omission: MS-DOS 6.22's driver mounts and lists a DoubleSpace
volume but answers every DS-compressed cluster with *Data error reading drive C*, so DoubleSpace volumes are gated under 7.1 only; and
MS-DOS 7.1's driver does not mount our DriveSpace 3 volumes (see `docs/CVF-CODECS-ON-DISK.md`). No
MS-DOS 6.0 / 6.2 (`DBLSPACE.BIN`) media were available.
