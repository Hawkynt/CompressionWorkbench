# 7z conformance vectors

Archives the real 7-Zip wrote, one per coder graph or header profile the 7z reader once
misread. `make_vectors.py` regenerates them and `manifest.tsv` (archive, entry, kind, size,
SHA-256) from a source tree it derives deterministically (`random.Random(20261030)`); the tree is
not checked in. `SevenZipConformanceVectorTests` reads every archive and checks the listing and
every byte against the manifest, so this proof runs on any machine, 7-Zip installed or not.

Produced with 7-Zip 26.04 (x64), Windows 11, 2026-10-30. Password archives use `S3cret!`.

| File | 7-Zip switches | What it pins |
| --- | --- | --- |
| `bcj.7z` | `-mf=BCJ` | x86 BCJ with the prefix-mask state of Bra86.c |
| `bcj2.7z` | `-mf=BCJ2` | the four-stream BCJ2 graph, packed streams listed explicitly |
| `bcj2-explicit.7z` | `-m0=BCJ2 -m1=LZMA2 -m2=LZMA -m3=LZMA -mb0:1 -mb0s1:2 -mb0s2:3` | the same graph spelled out |
| `ia64.7z` / `sparc.7z` | `-mf=IA64` / `-mf=SPARC` | branch converters liblzma and the SDK share |
| `delta4.7z` | `-mf=Delta:4` | a filter above the compressor (coder order, unpack-size index) |
| `swap4.7z` | `-m0=Swap4 -m1=LZMA2` | the same with an explicit chain |
| `lzma2-mx1.7z` | `-m0=LZMA2 -mx1` | LZMA2 chunks that continue state after a copy chunk |
| `hc-off.7z` | `-mhc=off` | a plain header |
| `solid-2f.7z` | `-ms=2f` | several folders, two files each |
| `times-all.7z` | `-mtc=on -mta=on` | creation and access times |
| `aes.7z` | `-pS3cret!` | AES content encryption: unpack sizes per coder, salt/IV flag bits |
| `aes-bcj2.7z` | `-pS3cret! -mf=BCJ2` | one AES coder per BCJ2 packed stream |
| `aes-mhe.7z` | `-pS3cret! -mhe=on` | an encrypted, LZMA-packed header with its folder CRC |
| `empty.7z` | `a` then `d` of the only file | an archive without a header (next-header size 0) |
| `anti.7z` | `u -u- -up0q3r2x2y2z0w2!anti.7z` | anti-items for a deleted file and directory beside a new file |
| `unix-links.7z` | 7-Zip 23.01 (Linux x64, in WSL Ubuntu): `7z a -snl` of `target.txt` (0640), `link.txt -> target.txt`, `sub/` (0750) with `inner.txt`, `linkdir -> sub` | Unix modes in the high attribute bits; symbolic links stored as their target (S_IFLNK). Not made by `make_vectors.py`: the commands are the ones in this row. |
