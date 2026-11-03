# Pasti reference vector

`UnionDemo-head.stx` is the first three track records of `UnionDemo.stx`. That file comes from
`UnionDemo.zip`, which the Pasti authors publish at
<http://pasti.fxatari.com/downloads/UnionDemo.zip>:

- the zip has SHA-256 `c6dd827a…f388`;
- `UnionDemo.stx` has SHA-256 `a6199cd5…83e4`.

The track-count byte at offset 10 was set to 3 to match the cut. Nothing else was changed.

The three records are cylinder 0 side 0 (nine standard 512-byte sectors, including the boot
sector) and cylinder 0 side 1 and cylinder 1 side 0, each with the demo's protection layout:
five 1024-byte sectors and one 512-byte sector.

`PastiExternalConformanceTests` uses the complete file. CI downloads it from the address above
and checks its hash.
