# SQLite Archive reference vector

| File | Origin |
| --- | --- |
| `sqlite3-cli.sqlar` | written on 2026-10-30 in WSL (Ubuntu 24.04) by the sqlite3 shell 3.45.1: `sqlite3 cli.sqlar -Ac hello.txt empty.txt sub`, with `hello.txt` = `hello sqlar\n` × 50, `empty.txt` empty, `sub/random.bin` 9000 bytes (byte i = `(i*2654435761 mod 2^32) >> 13`), every mtime 2026-10-26 10:00:00 UTC. `pragma integrity_check` says `ok`; page size 512, 5 pages. |

`SqlarTests` expects the rows the shell itself reports (`select name,mode,mtime,sz,length(data) from sqlar`)
and the SHA-256 of each source file.
