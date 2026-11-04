# Pack-Ice reference files

From the test corpus of Teemu Suutari's ancient (https://github.com/temisu/ancient,
`testing/test_files/`, revision `61cd9088a218ce43fd389f3f3c48f35fc70f7834`, BSD-2-Clause
repository), packed with the Pack-Ice versions their names give:

| File | Pack-Ice | Contents |
|---|---|---|
| `test_C1.ice_113` | 1.13, trailer `Ice!`, word stream | `alice29.txt` (Canterbury Corpus, public domain), 152089 bytes |
| `test_C1.ice_24` | 2.4, `ICE!`, byte stream | the same text |
| `test_neo.ice_211p` | 2.11, `Ice!`, picture mode | a NEOchrome image ancient's author generated, 32128 bytes |
| `test_neo.ice_234p` | 2.34, `ICE!`, picture mode | the same image |
| `test_neo2.ice_234p` | 2.34, `ICE!`, picture mode with a stored picture size | a second image, 16128 bytes |

The test compares each decoded file with the SHA-256 of ancient's unpacked original.
