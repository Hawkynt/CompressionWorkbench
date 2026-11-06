# partimage reference vectors

These three images were written by partimage 0.6.9 (Ubuntu 24.04 package `partimage` 0.6.9-11build2)
from one loop device. The device held a 2 MiB ext2 volume, built with
`mke2fs -t ext2 -b 1024 -L CWBPI`, that contains `numbers.txt` (`seq 1 2000`) and `hello.txt`.
The command was `partimage -b -z<n> -o -d save /dev/loopN ext2-z<n>`:

| File | Compression | SHA-256 |
| --- | --- | --- |
| `ext2-z0.000` | none | `a7cb208e1c45b883b53a86d9e4ecbb82bb82bbcfa81534bca00f01bb0f62b6c5` |
| `ext2-z1.000` | gzip | `b7ed250d93044895e88f2ef5767e36a8c7471674246a93227626dffe76ea5646` |
| `ext2-z2.000` | bzip2 | `71e4d05543825625cc7121316da1cec6b7f62a199a5b4fe4018afeef04913827` |

The volume itself has SHA-256 `8fd3cc1aab87625b6fe633c986516c6e3a1b34e7d7fb82983ff8f6ac08e3777e`.
`partimage restore` of `ext2-z0.000` onto a zeroed device reproduces it exactly.
