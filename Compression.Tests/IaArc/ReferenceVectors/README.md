# Internet Archive ARC reference vectors

| File | Origin |
| --- | --- |
| `example.arc`, `example.arc.gz`, `example-space-in-url.arc`, `bad.arc` | warcio's test data (`test/data/`, webrecorder/warcio master, fetched 2026-10-19), Apache-2.0. `bad.arc` is warcio's malformed-record sample (negative and non-numeric lengths); `example-space-in-url.arc` declares 1591 body bytes but holds 1579 (warcio returns the short body, we refuse). |
| `blackbook-head.arc.gz` | the first 23 gzip members (filedesc record + 22 URL records) of `IAH-20080430204825-00000-blackbook.arc.gz`, a version-1 ARC written by Heritrix in 2008, from richardlehane/webarchive `examples/` (master, Apache-2.0). Cut on member boundaries, so it is itself a valid `.arc.gz`. |
| `ours-v1.arc` | written by our `IaArcWriter` (fixed timestamps, see `IaArcTests.WriteOursV1`) and read by warcio; the writer must keep reproducing it. |
| `warcio-listing.txt` | warcio 1.8.1's reading of each file above: record type, declared length, SHA-256 of the body warcio locates (header line + LF, then that many bytes of the decompressed stream), URI. |

No real version-2 ARC is checked in: the only public samples found (the specification page's own
`IA-001102.arc` examples) declare lengths that do not match their bodies, and warcio rejects them too.
Version-2 parsing is covered by a constructed file in `IaArcTests`.
