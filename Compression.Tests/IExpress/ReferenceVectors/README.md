# IExpress reference package

`iexpress-lzx.exe` was built by Windows' own `C:\Windows\System32\iexpress.exe` (Windows 11 Pro
10.0.26200), never by CompressionWorkbench, from the SED file that `mkiexpress.py` writes
(`python mkiexpress.py <workdir> iexpress-lzx`): an extract-only package of three files (a short
text, 3000 seeded random bytes, 32000 bytes of a repeated pattern). It is stored gzip-compressed and
otherwise unchanged.

`oracle.tsv` is what Windows `expand.exe -F:*` writes from the package's `CABINET` resource (taken
out with 7-Zip 26.03): name, size and SHA-256 per file.
