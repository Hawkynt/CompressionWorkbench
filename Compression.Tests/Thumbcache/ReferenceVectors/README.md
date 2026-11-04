# Thumbnail cache reference vectors

| File | Origin |
| --- | --- |
| `windows_7-thumbcache_32-empty.db` | `tests/data/windows_7/thumbcache_32.db` of fox-it/dissect.thumbcache (commit `c73ae487`): the 24-byte header Windows 7 leaves when the cache holds no thumbnails (version 21). |
| `windows_10-thumbcache_1280-empty.db` | `tests/data/windows_10/thumbcache_1280.db`, same commit: the empty Windows 10 header (version 32, cache type 6). |
| `dissect-listing.txt` | dissect.thumbcache 1.11's view of all 49 `thumbcache_*.db` files of that corpus (Vista, 7, 8.1, 10, 11; the `idx` files excluded): per file its version and cache type, per entry the hash, the identifier as UTF-16LE hex, the thumbnail size and its SHA-256. dissect also yields the free-space record that follows the last entry (all-zero hash); those rows are left out. dissect returns everything from the padding to the end of the record; in 33 records that runs a few bytes past the entry's own data-size field, so the listing hashes dissect's data cut to that field. |

The two header-only files are 24 bytes of format fields. The populated databases are not
redistributed here: dissect.thumbcache is AGPL-3.0. To run the full comparison, point
`CWB_THUMBCACHE_CORPUS` at a checkout of its `tests/data` directory (the files are in Git LFS; the
`media.githubusercontent.com` URLs serve them).

No Windows 8 (version 30) database exists in that corpus; version 30 is read with the 8.1 layout,
which the libyal format description gives for both.
