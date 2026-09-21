# Structured pseudo-archive reference vectors

Bytes written by each format's own reference implementation, embedded into `Compression.Tests` and
asserted by `StructuredPseudoArchiveReferenceVectorTests`.

They exist because a writer checked only by our own reader passes while the two are wrong in the
same way — `CONTRIBUTING.md`, *The mutual-compensation trap*. Nothing here was produced by
CompressionWorkbench, so comparing against it is the cross-implementation parity gate that
`CONTRIBUTING.md` lists as Stage 2 acceptance #2, and reading it is the independent golden sample
Stage 0 asks for.

Both directions are covered per format:

- **theirs → ours** — our reader must recover the exact payload from bytes we did not write;
- **ours → theirs** — our writer must emit the very bytes the reference implementation emits for
  the same value.

The tests carrying these assertions are deliberately in **no** NUnit category, which puts them in
the `Core tests` step of `ci.yml` — the only step that gates a pull request. A vector asserted in
`ExternalInterop`, `EndToEnd` or any other advisory tier is excluded by the gate filter and proves
nothing about a merge. `StructuredPseudoArchiveExternalToolTests` runs the tools themselves against
our live output; that is a supplement, not the gate.

## The vectors

| File | Produced by | Direction it gates |
| --- | --- | --- |
| `messagepack-archive.msgpack` | python-msgpack 1.2.2, `packb(obj, use_bin_type=True)` | ours → theirs |
| `messagepack-types.msgpack` | python-msgpack 1.2.2 | theirs → ours |
| `messagepack-float32.msgpack` | python-msgpack 1.2.2, `use_single_float=True` | theirs → ours |
| `pickle-protocol4-archive.pickle` | CPython 3.13.1, `pickle.dumps(obj, protocol=4)` | both |
| `pickle-protocol0-document.pickle` … `pickle-protocol5-document.pickle` | CPython 3.13.1, one per protocol | theirs → ours |
| `json-archive.json` | CPython 3.13.1, `json.dumps(obj, indent=2)` | ours → theirs |
| `json-document.json` | CPython 3.13.1 | theirs → ours |
| `xml-document.xml` | CPython 3.13.1 `xml.etree.ElementTree` | theirs → ours |
| `xml-archive-elementtree.xml` | CPython 3.13.1 `xml.etree.ElementTree` | theirs → ours |
| `registry-export.reg` | Windows 11 (10.0.26200) `reg.exe export` | ours → theirs |
| `registry-types.reg` | Windows 11 (10.0.26200) `reg.exe export` | theirs → ours |
| `storable-nested.storable` | Perl 5.38.2, Storable 3.32, `nstore` | both |
| `storable-document.storable` | Perl 5.38.2, Storable 3.32, `nstore` | theirs → ours |
| `nrbf-string.nrbf`, `nrbf-hashtable.nrbf`, `nrbf-object-array.nrbf` | .NET Framework `BinaryFormatter` via Windows PowerShell 5.1 | theirs → ours |
| `windows9x-user.dat.gz` | Windows 95/98/Me, via [log2timeline/dfwinreg](https://github.com/log2timeline/dfwinreg) | theirs → ours |
| `windows-nt-ntuser.dat.gz` | Windows NT family, via log2timeline/dfwinreg | theirs → ours |

`generate.py` refreshes the Python-produced vectors; `python generate.py --registry` additionally
re-exports the two `.reg` vectors and needs Windows. `generate-storable.pl` and `generate-nrbf.ps1`
refresh the Perl and NRBF ones. The scripts are documentation of provenance — nothing in the build
runs them.

`.gitattributes` marks every vector `-text` so git cannot translate a newline inside one on
checkout. Two of them are XML and JSON and would otherwise look like ordinary text files.

## The two writers with no parity vector

- **XML.** Our creation path emits a namespaced `cwb:archive` envelope through `XmlWriter`, and no
  two XML writers agree on declaration quoting, attribute order and namespace placement, so there
  is nothing honest to compare it against. What is gated instead is that our *reader* handles a
  third party's rendering of that same envelope — `xml-archive-elementtree.xml`, which differs from
  our own output in all three of those respects — so the reader cannot quietly learn its sibling
  writer's dialect. That a real parser reads what we write is checked live, in the advisory tier.
- **CREG, REGF and NRBF** have no writer at all, so the question does not arise. They are read-only
  because nothing third-party writes a CREG hive, `reg.exe save` needs a privilege CI will not
  have, and .NET removed `BinaryFormatter` after .NET 5.

## Why these particular payloads

The writer vectors all encode one projection — the one `StructuredArchive.FromInputs` builds from
the inputs `dir/file.bin` (6 bytes), `dir/long.bin` (64 bytes) and `top.bin` (4 bytes):

```
{ "dir": { "file.bin": <6 bytes>, "long.bin": <64 bytes> }, "top.bin": <4 bytes> }
```

`long.bin` is 64 bytes so the `.reg` vector crosses `reg export`'s line-wrap column twice and pins
the continuation form, which a 6-byte payload never reaches.

Every name and payload in that tree is distinct. CPython's pickle memo is keyed on object identity,
so a repeated leaf makes CPython emit `BINGET` where our tree walker, which has no identity to
observe, emits the value again. Distinct leaves are what makes the two byte-comparable at all.

Perl randomises hash iteration order per process, so a multi-key hash serialises its pairs in a
different order every run and can never be pinned. The Storable parity vector is therefore a tree
of single-key hashes; it is the only input shape for which `nstore` and our writer are comparable.

The reader-only pickle vectors encode `{"dir": {"count": 7, "name": "value"}}` instead. Protocols 0
through 2 have no native `bytes` opcode and encode one as a `_codecs.encode` `GLOBAL`/`REDUCE`
pair, which our non-executing reader projects as a call record rather than a file; strings and
integers project identically on every protocol, so one set of assertions covers all six.

`messagepack-types.msgpack` sizes each entry to land on the format byte it is named after — 255 on
`uint8`, 256 on `uint16`, and so on — because `packb` always picks the shortest encoding. It covers
nil, both booleans, the whole integer width ladder to the 64-bit edges, float64, the str-versus-bin
split the 2.0 spec introduced, UTF-8 beyond the BMP, nesting, a non-string map key, and the
extension families including timestamp, the one negative type code the specification assigns.
`float32` needs `use_single_float=True` and so has a vector of its own.

## Redistributed hives

Verbatim copies of `test_data/USER.DAT` and `test_data/NTUSER.DAT` from log2timeline/dfwinreg at
revision `92ac103fe5072e83272463f61c9a1e65d4820997`, stored gzipped:

| Original | Size | SHA-256 of the uncompressed file |
| --- | --- | --- |
| `USER.DAT` | 159,776 | `9c018d8103685f10f5adf8839ce049a4d6e23d277af65ed45cfd723090f3ea16` |
| `NTUSER.DAT` | 524,288 | `490ba00a82808753d38e243b2aed2b9ad647e435a03f3b2e09a36bd34efd8607` |

dfwinreg is licensed Apache-2.0; the copies are redistributed under that licence and their origin
is recorded here as it requires. They are checked in rather than downloaded because the reader gate
has to run inside the pull-request test step, and a step that reaches the network fails open on a
runner that cannot.
