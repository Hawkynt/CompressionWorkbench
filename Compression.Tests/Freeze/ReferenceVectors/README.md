# Freeze reference files

From the test corpus of Teemu Suutari's ancient (https://github.com/temisu/ancient,
`testing/test_files/`, revision `61cd9088a218ce43fd389f3f3c48f35fc70f7834`, BSD-2-Clause
repository), both of `alice29.txt` from the Canterbury Corpus (public domain, 152089 bytes):

| File | Magic | Written by |
|---|---|---|
| `test_C1.F` | `1F 9F` | freeze 2.x |
| `test_C1_old.F` | `1F 9E` | freeze 1.x |

The test compares each decoded file with the SHA-256 of that text (`7467306e…700c38f0`).
