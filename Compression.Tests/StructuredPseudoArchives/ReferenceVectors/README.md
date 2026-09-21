# Structured pseudo-archive reference vectors

Frozen bytes produced by the reference implementation of each format, embedded into
`Compression.Tests` and asserted by `StructuredPseudoArchiveReferenceVectorTests`.

These exist because a writer checked only by our own reader passes while the two are wrong in the
same way — see `CONTRIBUTING.md`, *The mutual-compensation trap*. Every file here was written by a
third-party tool, so comparing our output against it is the cross-implementation parity gate that
`CONTRIBUTING.md` lists as Stage 2 acceptance #2.

The tests carrying these assertions are deliberately in **no** NUnit category, which puts them in
the `Core tests` step of `ci.yml` — the only step that gates a pull request. A vector asserted in
`ExternalInterop`, `EndToEnd` or any of the other advisory tiers is excluded by the gate filter and
proves nothing about a merge.

## Provenance

| File | Produced by | Notes |
| --- | --- | --- |
| `messagepack-archive.msgpack` | `msgpack` 1.2.2 for Python, `msgpack.packb(obj, use_bin_type=True)` | Writer parity vector |
| `pickle-protocol4-archive.pickle` | CPython 3.13.1, `pickle.dumps(obj, protocol=4)` | Writer parity vector |
| `pickle-protocol0-document.pickle` … `pickle-protocol5-document.pickle` | CPython 3.13.1, `pickle.dumps(doc, protocol=N)` | Reader vectors, one per protocol the reader accepts |
| `registry-export.reg` | Windows `reg.exe export` (Windows 11, 10.0.26200) | Writer parity vector |
| `windows9x-user.dat` | Windows 95/98/Me, as published by [log2timeline/dfwinreg](https://github.com/log2timeline/dfwinreg) | Reader golden sample |

`generate.py` regenerates everything except the hive; `python generate.py --registry` additionally
re-exports the `.reg` vector and needs Windows.

### `windows9x-user.dat`

A verbatim copy of `test_data/USER.DAT` from log2timeline/dfwinreg at revision
`92ac103fe5072e83272463f61c9a1e65d4820997`, 159,776 bytes,
SHA-256 `9c018d8103685f10f5adf8839ce049a4d6e23d277af65ed45cfd723090f3ea16`.

dfwinreg is licensed Apache-2.0; the copy is redistributed under that licence and its origin is
recorded here as the licence requires. It is checked in rather than downloaded because the reader
gate has to run inside the pull-request test step, and a step that reaches the network fails open
on a runner that cannot.

## Why these particular payloads

The three writer vectors all encode the same projection — the one
`StructuredArchive.FromInputs` builds from the inputs `dir/file.bin` (6 bytes), `dir/long.bin`
(64 bytes) and `top.bin` (4 bytes):

```
{ "dir": { "file.bin": <6 bytes>, "long.bin": <64 bytes> }, "top.bin": <4 bytes> }
```

`long.bin` is 64 bytes so that the `.reg` vector crosses `reg export`'s line-wrap column twice and
pins the continuation form, which a 6-byte payload alone would never reach.

Every name and payload in the tree is distinct. CPython's pickle memo is keyed on object identity,
so a repeated leaf makes CPython emit `BINGET` where our tree walker, which has no identity to
observe, emits the value again. Distinct leaves are what makes the two byte-comparable.

The reader-only pickle vectors encode `{"dir": {"count": 7, "name": "value"}}` instead. Protocols 0
through 2 have no native `bytes` opcode and encode one as a `_codecs.encode` `GLOBAL`/`REDUCE`
pair, which our non-executing reader projects as a call record rather than a file; strings and
integers project identically on every protocol, so one set of assertions covers all six.
