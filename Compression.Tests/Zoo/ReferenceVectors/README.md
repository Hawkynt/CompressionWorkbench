# Zoo reference archives

Every file here was written by Zoo itself, never by CompressionWorkbench.

## Zoo 2.10 (Rahul Dhesi; `bitplane/zoo` master, built with `make linux` on Ubuntu 24.04 / WSL)

| File | Command | What it pins |
| --- | --- | --- |
| `lzh-zoo210.zoo` | `zoo ah lzh-zoo210.zoo text.txt runs.txt hello.txt` | packing method 2 (LZH): `text.txt` is `The quick brown fox jumps over the lazy dog. ` × 50 (2250 bytes), `runs.txt` is 20000 × `a`; `hello.txt` (`hello zoo\n`) was stored (method 0) because LZH did not shrink it. `zoo -test` reports the archive OK. |
