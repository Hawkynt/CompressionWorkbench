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
| `v3.1-lzma-le.sqfs`, `v3.1-lzma-be.sqfs` | mksquashfs from the sqlzma build of squashfs3.3 | `-le` / `-be` |
| `v3.0-hsqt-le.sqfs`, `v3.0-hsqt-be.sqfs` | mksquashfs-lzma from the LZMA build of squashfs3.0 ("damn small" variant) | `-le` / `-be` |

The sources are the release tarballs from https://sourceforge.net/projects/squashfs/files/squashfs/.
The sqlzma build is squashfs3.3 with LZMA SDK 4.57 and Junjiro Okajima's sqlzma patches
(`sqlzma3.3-457`), taken as firmware-mod-kit carries it (`src/others/squashfs-3.3-lzma`,
https://github.com/rampageX/firmware-mod-kit) and built with today's gcc (`-fcommon -fgnu89-inline
-w -include sys/sysmacros.h -include stdint.h`, `g++ -fpermissive`). It writes the magic `shsq`
(`qshs` big-endian) and compresses each block with zlib or LZMA; an LZMA block starts with the
LZMA SDK's 13-byte header (properties 0x5D, dictionary size, 64-bit length).
The LZMA build of squashfs 3.0 (firmware-mod-kit `src/squashfs-3.0-lzma-damn-small-variant`, built
the same way) writes the magic `hsqt` (`tqsh` big-endian) and compresses every block with LZMA
behind a four-byte header (pb, lc, lp, fast bytes); its `unsquashfs-lzma -dest` is the oracle for
those two images. It does not byte-swap the uid/gid table of a big-endian image, so its rows for
`v3.0-hsqt-be.sqfs` show uid 1234 as 3523477504; the tests take owners from the little-endian image.
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
run as root (for the sqlzma and hsqt images, which 4.6.1 cannot read, their builds' own unsquashfs): path, type, size, mode, uid, gid, mtime and SHA-256 (files) or target (symlinks).
1.x and 2.x symlink inodes have no time field, so unsquashfs stamps those symlinks with the time of
the extraction, and the sqlzma unsquashfs (3.3) never sets a symlink's time; the tests skip that
column for them. The sqlzma images were made as root, so their files are owned by 0:0 except
where the payload sets an owner. 7-Zip 23.01 lists the same 307 files and 7
folders in each image.

squashfs-tools is GPL-2.0; it was only run, never linked or copied. The images hold only the
payload above.
