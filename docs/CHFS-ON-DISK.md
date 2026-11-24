# CHFS on disk

CHFS (Chip File System) is NetBSD's file system for raw NOR and NAND flash, from the
University of Szeged. This page is the specification `FileSystem.Chfs` is written from.

**Sources (rung 1).** NetBSD's BSD-licensed `sys/ufs/chfs/media.h` and `ebh_media.h`, which
the reader converts, and `usr.sbin/makefs/chfs.c` and `usr.sbin/makefs/chfs/chfs_mkfs.c`,
which the writer converts (notice: `THIRD-PARTY-NOTICE.Chfs.txt`). `sys/ufs/chfs/` and
`sys/miscfs/specfs/spec_vnops.c` explain the kernel's behaviour below. All integers are little-endian; every CRC is CRC-32 (IEEE)
with seed 0.

## Eraseblocks

An image is a sequence of eraseblocks. Each one starts with two headers, one page apart:

| At | Header | Fields |
| --- | --- | --- |
| 0 | erase counter | magic `0x53454452` ("RDES"), CRC of the erase count, erase count. The rest of the page is `0xFF` |
| one page | NOR form, 8 bytes | CRC of the logical eraseblock id, then that id |
| one page | NAND form, 16 bytes | CRC of id and serial, the logical id, and a 64-bit serial |

Neither size is recorded. The eraseblock size is the distance between erase-counter
headers. The page size is where the second header starts. Its CRC (over 4 or 12 bytes)
tells NOR from NAND. Nodes begin at two pages into the block. An eraseblock that is all
`0xFF` is unused.

`makefs` defaults to NAND with 2048-byte pages and 128 KiB eraseblocks. `-o
mediatype=0,pagesize=…,eraseblock=…` chooses others.

## Nodes

Every node starts with a 12-byte header: magic `0x4AF1` (2), type (2), length (4), and the
CRC of those 8 bytes (4). Nodes are padded to 4 bytes. `0xFFFF` where a magic would be
means the rest of the eraseblock is erased.

| Type | Layout after the header |
| --- | --- |
| 1 vnode (64 bytes) | vno (8), version (8), uid, gid, mode, size, atime, mtime, ctime, a legacy dsize (4 each), node CRC over the 60 bytes before it |
| 2 data (48 + data) | vno (8), version (8), file offset (8), data length (4), data CRC (4), node CRC over the 44 bytes before it, then the data |
| 3 directory entry (52 + name) | vno (8), parent vno (8), version (8), mctime (4), name length (1), type (1), 2 unused, name CRC (4), node CRC over the 48 bytes before it, then the name |
| 4 padding | — |

The root is vnode 2, and `makefs` writes no vnode for it. The newest vnode version gives a
file's metadata. Data nodes are laid over each other oldest first, and the vnode's size
cuts the result. A symlink's data is its target. The newest directory entry for a
(parent, name) pair names the child.

`makefs` stores `st_uid` in the gid field and `st_gid` in the uid field. It writes each
hard-linked file once, under its first name only. Data nodes are at most one page long.
It copies a name without its terminator and then measures it with `strlen`, so a name
can carry trailing bytes left over from earlier memory. In the NAND fixture, `empty-dir`
was stored as the 14-byte name `empty-dirempty`, with the name CRC covering all 14 bytes.
That is the name the image holds, and the name the reader reports.

## What the kernel writes

Measured on NetBSD 9.5 after mounting an image, creating files and a directory, and
unmounting. Each vnode numbers its own vnode and data nodes from 1. Data nodes are up to
4 KiB long, the file system's block size. A vnode node with the new size follows the data.
Padding nodes (type 4) fill the end of a page or an eraseblock. Name bytes past the name are
zero. The kernel writes an erase-counter header on every eraseblock it has checked, so a free
block can hold an erase-counter header and nothing else, not even an eraseblock header.

## Data nodes shorter than 4 KiB

`makefs` cuts file data into one node per page and writes every data node as version 0. NetBSD
9.5 reads such files back wrongly when they need more than one node. The size is right, but
every second 2 KiB of the file reads as zeros. On a 168,894-byte `jot 30000` file, 2 KiB chunks
1, 3, 5 … 81 were wrong. Numbering the 2 KiB nodes 1, 2, 3 … does not help: every file of more than
2 KiB still read back wrong. Nodes of 4 KiB, the size the kernel writes, numbered as the
kernel numbers them, read back right. So the writer cuts data into 4 KiB nodes by default.
The reader lays data nodes over each other in version order and then in image order, so it
returns what `makefs` was given.

## What the implementation does with it

* **Read:** every header, node, data and name CRC is checked, and a mismatch is refused.
  It reads either media type and any geometry, and free blocks that hold only an
  erase-counter header.
* **Refused, not replayed:** entries whose vnode number is 0. Only a mounted file system
  writes them, to remove a name.
* **Create:** `ChfsWriter` lays an image out as `makefs` does. Entries are sorted by name.
  Each directory's entries get a vnode node, then a directory-entry node, then data nodes for
  anything but a directory. Subdirectories follow, depth first. A node that does not fit the
  rest of an eraseblock starts the next one. Gaps are 0xFF. There are three differences. Data
  nodes are 4 KiB and numbered, as described above. Names are stored exactly. Owner and group go
  in the fields the kernel reads.
  Given the tree of a `makefs` image and page-sized data nodes, the writer reproduces that image
  byte for byte, apart from the data-node versions and their node CRCs. This holds for both the
  NAND and the NOR fixture.
* **Modify:** add, replace and remove lay the whole image out again with the same geometry.
  The relayout keeps names, contents, modes, owners, the three times, link targets, device
  numbers, directory sizes and each eraseblock's erase count. Images with hard links are
  refused, because a relayout would split them.

## Proof

The only CHFS implementation is NetBSD's kernel, and it reaches media only through
`flash(4)`. On a machine qemu emulates, the only flash NetBSD attaches is `nandemulator(4)`.
That driver hard-codes 32 MiB of NAND with 2 KiB pages and 128 KiB eraseblocks ("hardcode
these now, make it configurable later" in `sys/dev/nand/nandemulator.c`). That is also
`makefs`'s default geometry.

* **NetBSD 9.5 mounts it.** The test image is `NetBSD-9.5-amd64-bios-install.img.gz` from
  cdn.netbsd.org, checked against its published SHA512. The test boots it single-user under
  qemu, runs `modload nandemulator` and `modload chfs`, copies the image onto
  `/dev/rflash0` and runs `mount_chfs /dev/flash0 /mnt`. The opt-in tests in
  `ChfsNetBsdTests` (`CWB_NETBSD9_IMAGE`) then check two things. First, every entry of a
  writer image lists with the type, permissions, owner, group, mtime and SHA-256 or link
  target it was written with. Second, a `makefs` image after our add and remove lists every
  entry, with the right content. While the image is mounted, the kernel writes a file, a
  directory and a 300 KB copy. Our reader reads the unmounted flash back with those files
  correct.
* **NOR and other geometries** are proven only by matching `makefs` output byte for byte
  (with page-sized data nodes, apart from data-node versions). No emulated NOR flash attaches
  to NetBSD.

## Why NetBSD 10 and 11 hang

`chfs_mount` calls `VOP_OPEN` on the device vnode without locking it. NetBSD 10 changed
`spec_open` so that it always unlocks the vnode, opens the device and locks it again. On a
vnode nobody had locked, that leaves the vnode locked, and the
`vn_lock` at the start of `chfs_mountfs` then waits on that lock forever. This is the
`tstile` wait that was measured on 10.1 and 11.1: `mount_chfs` sits in state `D` with 0 CPU time,
and anything that touches `/mnt` blocks behind it. NetBSD 9's `spec_open` unlocks only while
loading a driver module, so the mount goes through.

| Route | Result |
| --- | --- |
| NetBSD 11.1 amd64 live image, `makefs` image on `/dev/rflash0`, `mount_chfs` | Never returns (the lock above). The same happens on blank flash. |
| NetBSD 10.1 amd64 live image, same steps | `mount_chfs` waits on `tstile` with 0 CPU time; the guest stops responding soon after. |
| NetBSD 9.5 amd64 BIOS install image, same steps | Mounts, reads, writes and unmounts (above). |
| Other `nandemulator` geometry | Not available; the driver takes no properties or sysctls. |
