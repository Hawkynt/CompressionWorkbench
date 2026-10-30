# 7z reference archives from libarchive

Taken from libarchive's test suite (`libarchive/test/*.uu`, BSD-2-Clause, Copyright (c) 2003-2023
Tim Kientzle and the libarchive contributors), uudecoded: tag `v3.7.7` where the file exists there,
otherwise master at `348e5a2e27503f5bf114e298e2bd9ddcd59ab48e`. They were written by 7-Zip. Each
`manifest-*.tsv` lists the members with size and SHA-256 as 7-Zip 26.04 extracts them.

| File | Coders | Manifest |
|------|--------|----------|
| `test_read_format_7zip_lzma1.7z` | LZMA | `manifest-lzma.tsv` |
| `test_read_format_7zip_lzma1_2.7z` | LZMA, several members in one folder | `manifest-lzma.tsv` |
| `test_read_format_7zip_delta_lzma1.7z` | Delta:1 + LZMA | `manifest-lzma.tsv` |
| `test_read_format_7zip_delta4_lzma1.7z` | Delta:4 + LZMA | `manifest-lzma.tsv` |
| `test_read_format_7zip_lzma1_lzma2.7z` | LZMA and LZMA2 folders | `manifest-lzma.tsv` |
| `test_read_format_7zip_lzma1_powerpc.7z` | PPC + LZMA (master) | `manifest-lzma.tsv` |
