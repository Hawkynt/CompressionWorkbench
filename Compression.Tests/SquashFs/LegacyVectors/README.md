# SquashFS 1.x-3.x reference images

Real images written by the original mksquashfs releases, never by CompressionWorkbench:

| Image | Writer | Options |
| --- | --- | --- |
| `v1.0-le.sqfs`, `v1.0-be.sqfs` | mksquashfs from squashfs1.3r3 | `-le` / `-be` |
| `v1.0-le-check-uncompressed.sqfs` | mksquashfs from squashfs1.3r3 | `-le -check_data -noI -noD` |
| `v2.0-le.sqfs` | mksquashfs from squashfs2.2-r2 | `-le -2.0` |
| `v2.1-le.sqfs`, `v2.1-be.sqfs` | mksquashfs from squashfs2.2-r2 | `-le` / `-be` |
| `v3.0-le.sqfs` | mksquashfs from squashfs3.0 | `-le` |
| `v3.1-le.sqfs`, `v3.1-be.sqfs` | mksquashfs from squashfs3.4 | `-le` / `-be` |
| `v3.1-le-check-nofrag.sqfs` | mksquashfs from squashfs3.4 | `-le -check_data -no-fragments -b 4096` |
| `v3.1-le-uncompressed.sqfs` | mksquashfs from squashfs3.4 | `-le -noI -noD -noF` |

The sources are the release tarballs from https://sourceforge.net/projects/squashfs/files/squashfs/.
Those releases target 32-bit x86, so they were built with `gcc -m32` and the flags today's
compilers need for that old C (`-fgnu89-inline -fcommon -include sys/sysmacros.h`, plus
`-Wno-error` for implicit declarations, int conversions and pointer types). squashfs1.3r3's
`mksquashfs.c` also assigns through a cast (`(T *) inode = get_inode(...)`, a GCC extension later
removed); those two statements were rewritten as `(T *) (inode = get_inode(...))`, which does the
same thing.

`make-fixtures.py` builds the payload (text, an empty file, a multi-block text, 9000
incompressible bytes, nested and empty folders, 300 long-named files in one folder, a relative and
a dangling symlink, files with modes 0640/0600 and owners 1234:4321 and 0:0, every time set to
1200000000), runs each writer, and records the oracle: `python3 make-fixtures.py <workdir> <src-root>`.

`oracle.tsv` is what unsquashfs 4.6.1 (squashfs-tools, Ubuntu 24.04) extracts from each image when
run as root: path, type, size, mode, uid, gid, mtime and SHA-256 (files) or target (symlinks).
1.x and 2.x symlink inodes have no time field, so unsquashfs stamps those symlinks with the time of
the extraction; the tests skip that column for them. 7-Zip 23.01 lists the same 307 files and 7
folders in each image.

squashfs-tools is GPL-2.0; it was only run, never linked or copied. The images hold only the
payload above.
