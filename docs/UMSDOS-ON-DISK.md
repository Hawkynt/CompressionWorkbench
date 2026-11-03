# UMSDOS on disk

This is what the `Umsdos` reader relies on. It was written from Linux 2.4.0's
`include/linux/umsdos_fs.h`, `fs/umsdos/mangle.c` and `fs/umsdos/dir.c` (GPL-2.0),
which were read as a specification and not copied. It was then checked against
Slackware 8.1's ZipSlack, a UMSDOS installation shipped as a ZIP of its DOS files.

## Layout

UMSDOS is an ordinary MS-DOS FAT volume. Each directory Linux manages holds one
extra DOS file, `--linux-.---` (the EMD). The EMD is a sequence of 64-byte records.
An entry takes `⌈(36 + name length) / 64⌉` records, and a first byte of 0 marks a
free record.

| Offset | Size | Field (i386 types) |
|---|---|---|
| 0 | 1 | name length; 0 = unused |
| 1 | 1 | flags: 1 hidden, 2 pseudo hard link |
| 2 | 2 | link count |
| 4 | 2 | uid |
| 6 | 2 | gid |
| 8 | 4 | atime |
| 12 | 4 | mtime |
| 16 | 4 | ctime |
| 20 | 2 | rdev |
| 22 | 2 | mode (`stat` type and permission bits) |
| 24 | 12 | spare |
| 36 | ≤ 220 | name, NUL-padded |

## Which DOS file holds an entry

Let *n* be the entry's record number, its byte offset ÷ 64.

- A name MS-DOS can store (8.3, legal characters, no clash with the scheme below)
  is stored under that name, which DOS upper-cases.
- Any other name is stored under a *mangled* name. The base is the start of the
  name with characters DOS rejects replaced. The extension is three characters:
  `T₃[(n >> 10) & 31]`, `T₁₂[(n >> 5) & 31]`, `T₁₂[n & 31]`.
  - `T₁₂` is `_123456789abcdefghijklmnopqrstuv`.
  - `T₃` is `{}()!`^&@` followed by `T₁₂`.

  So record 4 gets `.{_4`, and ZipSlack's `boot-menu.b` is the DOS file
  `BOOT-MEN.{_4`. Only the extension identifies the record, so the reader matches on
  it and never has to reproduce the base.

## Entry types

- Directories recurse.
- A symbolic link's DOS file holds the target path.
- A pseudo hard link (flag 2) holds the path, from the DOS root, of the real file.
  The real file is a hidden entry (flag 1).
- Devices, FIFOs and sockets have no DOS file.
- Hidden entries are not listed.
- A directory without an EMD is seen by Linux with its DOS names.
