# CHFS on disk

CHFS (Chip File System) is NetBSD's file system for raw NOR and NAND flash, from the
University of Szeged. This page is the specification `FileSystem.Chfs` is written from.

**Sources (rung 1).** NetBSD's BSD-licensed `sys/ufs/chfs/media.h` and `ebh_media.h`, which
the reader converts (notice: `THIRD-PARTY-NOTICE.Chfs.txt`), and `usr.sbin/makefs/chfs/`
for how images are laid out. All integers are little-endian; every CRC is CRC-32 (IEEE)
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

## What the implementation does with it

* **Read:** every header, node, data and name CRC is checked, and a mismatch is refused.
  It reads either media type and any geometry.
* **Refused, not replayed:** entries whose vnode number is 0. Only a mounted file system
  writes them, to remove a name.
* **Read-only.** Nothing outside this package has been found to read a CHFS image back.
  So no writer can be proven, and none is offered. The routes tried are listed below.

## Routes tried for a CHFS reader to prove a writer against

The only CHFS implementation is NetBSD's kernel. It reaches media only through `flash(4)`.
On a machine qemu emulates, the only flash NetBSD attaches is `nandemulator(4)`.

| Route | Result |
| --- | --- |
| NetBSD 11.1 amd64 live image (SHA512 checked), `modload nandemulator chfs`, a `makefs -t chfs` image copied onto `/dev/rflash0`, then `mount_chfs /dev/flash0 /mnt` | `mount_chfs` never returns, and the guest stops responding. The same happens on blank flash. |
| NetBSD 10.1 amd64 live image (SHA512 checked, from archive.netbsd.org), same steps | The `makefs` image is on the flash: reading back `/dev/rflash0` shows its `RDES` header, and the rest of the flash reads 0xFF, erased. `mount_chfs` then sits in state `D` on wait channel `tstile` (blocked on a kernel lock) with 0 CPU time for at least 80 s. Anything that touches `/mnt` blocks behind it, and the guest stops responding soon after. |
| Other `nandemulator` geometry | Not available. The driver hard-codes 32 MiB, 2 KiB pages, 64 pages per block and 64 spare bytes ("hardcode these now, make it configurable later" in `sys/dev/nand/nandemulator.c`). It takes no properties or sysctls. |
| An erased device first | `flashctl /dev/flash0 erase 0 33554432` succeeds. The flash already reads 0xFF before that, so this changes nothing. |
| NetBSD 9.4 | No amd64 live image is published for 9.4, so it was not tried. |
| evbarm, or other emulated flash | Not tried. It would need an ARM machine that qemu emulates with NAND or NOR flash that NetBSD attaches to `flash(4)`, and no such pairing was found. |

The reader stays proven against `makefs` output in both media types (fixtures and the
opt-in live test). The writer waits for a kernel that mounts CHFS from emulated flash.
