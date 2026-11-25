# btoa 4.0 vectors

Each `<name>.btoa` is what btoa 4.0 wrote for `<name>.bin`, and atob 4.0 restored every
`.bin` from it byte for byte.

## Tools

btoa.c and atob.c version 4.0 (Paul Rutter, Joe Orost) as distributed in
[mit-athena/tarmail](https://github.com/mit-athena/tarmail), built in WSL (Ubuntu 24.04,
gcc 13.3) for a 32-bit `long`, the size the programs were written for:

```
gcc -m32 -std=gnu89 -w -include stdlib.h -include string.h -include unistd.h -o btoa btoa.c
gcc -m32 -std=gnu89 -w -include stdlib.h -include string.h -include unistd.h -o atob atob.c
```

The only source change: atob's temporary file moved from `/usr/tmp/` to `/tmp/`. Built for
a 64-bit `long` the programs do not produce the format (the word accumulator and the
rotating checksum keep bits above 32).

## Inputs

| File | Content |
|---|---|
| `empty.bin` | 0 bytes |
| `one.bin` | `A` |
| `hello.bin` | `Hello, btoa!\n` (13 bytes: one padded group) |
| `zeros312.bin` | 312 zero bytes: 78 `z`, so the data line ends exactly at the wrap |
| `ff64.bin` | 64 bytes 0xFF: the largest group, `s8W-!` |
| `bytes1024.bin` | bytes 0..255 four times |
| `random5003.bin` | 5003 bytes from Python `random.Random(1234)` |
