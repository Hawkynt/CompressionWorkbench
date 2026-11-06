# BCM 2.x — on-disk notes

BCM 2.x (Ilya Muravyov, `BCM2` files) is refused: `Bcm` reads and writes BCM 1.x only. This page
records what was measured about the 2.x format, so the refusal can be checked and the work picked
up again if the source appears.

## Sources

- **No source.** The `encode84/bcm` repository is gone. Its last archived state (Software
  Heritage, May 2021) and every fork found (`geekmaster/bcm`, `FS-make-simple/bcm`, `tansy/bcm`
  and others) stop at 1.65. Distribution packages (`bcm-git` on AUR) build from that repository.
  `bcm203.zip` from compressme.net contains only `bcm.exe`. In the encode.su thread announcing the
  2.00 alphas (November 2020) the author said the final 2.x would be open source; no such release
  was found.
- **The oracle.** `bcm.exe` v2.03 (27 Feb 2023, 284 160 bytes), run as a black box on inputs of
  our choosing.

## The file

All integers are a bijective base-128 varint: each byte adds `(b & 127) << 7k`, a set top bit
continues, and each continuation also adds `1 << 7(k+1)`. So `ff 00` = 255, `80 01` = 256,
`81 01` = 257, `80 ff fe 0e` = 33 554 432.

    "BCM2"
    varint  block size   the -b size, cut to the file length (empty file: 32 MiB, the default)
    varint  file length
    per block:
      varint  record length (0 ends the file)
      u32 LE  CRC-32C (Castagnoli) of the block's original bytes
      record payload
    varint 0

Checked on 20 inputs and switch combinations. Every file parsed to its last byte. The CRC-32C
matched every block, single-block and `-b4k` alike. Output depends neither on the thread count
(`-t1` against the default) nor on the run.

## A record

    varint  n       length of the bytes the transform stage sees
    byte    method
    varint  index   only when method bit 0 is set: the BWT primary index
    ...     coded bytes

Method bits seen:

| Bit | Seen when | Meaning (measured) |
|---|---|---|
| `0x01` | every compressed block | BWT + context-mixing coder; an index follows |
| `0x10` | runs of at least `-l` bytes (default 128) | LZP applied: `n` shrinks (4096 zeros → 8, two copies of 0..255 → 261) |
| `0x20` | default; gone with `-x-` | x86 filter on. With it on, a repetitive E8-call pattern kept `n` = 4800; with `-x-` LZP took it to 16 |
| none (`0x00`) | incompressible input (600 bytes of noise; 1–16 byte inputs) | stored: `n` bytes follow verbatim |

**The BWT stage is pinned.** With LZP and the filter off, the stored index equals `libsais_bwt`'s
return value for the same bytes:
- 3000 bytes of words: 1136;
- two copies of 0..255: 2;
- 4096 zeros: 4096.

`Compression.Core` already computes this BWT byte for byte. The BCM 1.65 and libbsc fixtures pin
it, so there was nothing to convert from libsais.

## What is not known: the coder

Everything after the index is arithmetic-coded by a context-mixing model. For 2.00 the author
described it as "completely rewritten" with "brand new tricks". The stage cannot be read from
outside: a model's predictions are visible only through the bits it codes.

Hypothesis tested and **refuted**: the coded bytes are BCM 1.65's back end (same coder, counters,
SSE and flush) over the libsais BWT output, with or without 1.65's in-band size and index.
- Inputs: 3000 bytes of words, two copies of 0..255, 600 bytes of `ab` and 600 bytes of noise,
  all with `-x-`.
- Comparison: 1.65's coder applied to the libsais BWT of the same bytes against 2.03's coded
  bytes.
- Result: not a single 3-byte run in common.

LZP is unpinned as well, beyond its minimum-length switch and the observed shrinkage: its hash,
table and escape coding were not measured.

## Verdict

The framing, checksum, method flags and BWT are pinned. The LZP stage and the context-mixing
model are not, and both decide every coded byte. Without them nothing can be decoded or written,
so `BCM2` files stay refused. That would change if the 2.x source were published, or if a 2.x
decoder could be made to show its probabilities. A version that only decodes cannot do that.
