# EC-SBX reference vectors

Written on 2026-10-30 in WSL by blkar 7.2.7 (`blkar-v7.2.7-x86_64-unknown-linux-musl`, MIT,
<https://github.com/darrenldl/blockyarchive>) from `fox.bin` (3000 bytes, byte i = `(i*31 + (i >> 7)) mod 256`,
SHA-256 `21e737f57aa476a53d72507d3f25364f4b62e86fde73c6ce4fe9232846169def`):

| File | Command | `blkar show` |
| --- | --- | --- |
| `fox-v18.ecsbx` | `blkar encode --sbx-version 18 --rs-data 5 --rs-parity 2 --burst 3 fox.bin fox-v18.ecsbx` | version 18, 5 data / 2 parity shards, file size 3000, name fox.bin; 45 blocks (3 metadata, 30 data, 12 parity) |
| `fox-v17.ecsbx` | `blkar encode --sbx-version 17 --rs-data 10 --rs-parity 3 --burst 1 fox.bin fox-v17.ecsbx` | version 17, 10 data / 3 parity shards, file size 3000, name fox.bin |
