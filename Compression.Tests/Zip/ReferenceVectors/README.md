# ZIP reference archives

Every file here was written by a third-party archiver, never by CompressionWorkbench.

| File | Writer | Command | What it pins |
| --- | --- | --- | --- |
| `xz-7zip2603.zip` | 7-Zip 26.03 (x64, Windows) | `7z a -tzip -mm=xz xz-7zip2603.zip text.txt hello.txt` | method 95 (XZ): `text.txt` is `The quick brown fox jumps over the lazy dog. ` × 50 (2250 bytes, CRC-32 `5FE03AFB`) as a complete .xz stream; `hello.txt` (`hello xz\n`) fell back to Store |
