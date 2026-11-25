# DIET data file vectors

`<name>.diet` is what DIET 1.45f made of `<name>.bin` (`DIET -B <file>`, which compresses a
data file in place). DIET 1.45f: sac.sk `diet145f.zip` (sha256 `2af6f269…c9ff715c`), run in
DOSBox-X (WSL, SDL dummy video driver, no window) with the files on a mounted directory.

| File | Content | DIET's report |
|---|---|---|
| `T16K` | 16000 bytes of words from a fixed list (Python `random.Random(5)`) | 16000 to 3984 bytes |
| `ODD` | 8193 bytes, same source | 8193 to 2130 bytes |
| `Z50K` | 50000 zero bytes | 50000 to 634 bytes |
| `MIX20K` | 20000 bytes, 30 % random, the rest `A` | 20000 to 11386 bytes |

The inputs are generated test data of this repository.
