# UltraCompressor II reference archives

Written by the original UltraCompressor II (r2, PRO 2.3 and 2.37 beta, run in DOSBox-X), never by
CompressionWorkbench. Taken unmodified from `testdata/oracle/` of
[klauspost/uc2](https://github.com/klauspost/uc2) at revision
`53622f89ada1cf0d4a69f91b653bdd44ec5b0f92` (LGPL-3.0, `LICENSE.txt` beside this file); `README.txt`
is that directory's own description of how each archive was made, and `sums.txt` lists the first 8
bytes of the SHA-256 of every stored file revision.

| File | Made by | What it pins |
| --- | --- | --- |
| `r2_def.uc2` | UC r2 `UC A C:\DEF *.*` | default method, custom masters over the supermaster |
| `r2_prot.uc2` | UC r2 `UC A -S -P` | damage-protection area after the component; subdirectories |
| `r2_rev.uc2` | UC r2 `UC A` then `UC A -I` twice | three revisions of one name; the newest is listed |
| `r2_tf.uc2`, `r2_tst.uc2`, `r2_tt.uc2` | UC r2 `-TF`, `-TST`, `-TT` | fast, tight and super-tight levels (the tight levels may pick delta methods) |
| `v23_def.uc2` | UC PRO 2.3 | made-by 203 |
| `v237b_lfn.uc2` | UC 2.37b `UC A -S` | `AIP:Win95 LongN` tags, CP437 names (`Café crème.txt`) |
| `v237b_tt.uc2` | UC 2.37b `-TT` | 2.37b's encoder at the tight level |
