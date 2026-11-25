# StuffIt method 6 (fixed Huffman) on disk

Method 6 is what StuffIt 1.x writes for its "Faster" setting. The Unarchiver (XADMaster) names the
method but does not decode it. This page is the specification `StuffItFixedHuffmanDecoder` is
written from.

## 📚 Sources

| What | Where |
|---|---|
| Format description and oracle | [ObsoleteMadness/StuffIt-Go](https://github.com/ObsoleteMadness/StuffIt-Go) revision `915dc497408051741f5802cf22c289bbe2369cc1`, `stuffit/method6.go` (LGPL-2.1). The repository grants no "or later" option, so its code is not converted; the layout below was written from it and our decoder is checked against its extractor's output |
| Real sample | the same repository, `samples/v1-fhf-faster.sit`, SHA-256 `d3f14868f8742a13d8d2cc914b59b173789c7c2f8a7bb293e8d4a3cc17d7211d`: two entries, three method-6 forks |
| PackBits | Apple Technical Note TN1023, *Understanding PackBits* |

## 🧱 Layout

A method-6 fork is a sequence of blocks, read until the fork's declared (uncompressed) length has
been produced. All integers are big-endian.

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | `size`, signed, counting these four bytes |

- **`size` < 0**: a PackBits-only block. The next `-size - 4` bytes are PackBits.
- **`size` ≥ 0**: a coded block, at least 10 bytes:

| Offset | Size | Field |
|---|---|---|
| 4 | 4 | `count`: number of PackBits bytes the code carries |
| 8 | 2 | `entries`, signed, 0..256 |
| 10 | `entries` | translation table: byte `i` is what code `i` stands for |
| 10 + `entries` | rest of block | prefix code, most-significant bit of each byte first |

Codes are read until `count` bytes are out or code 256 (end of block) is read. Table entries a block
does not define keep the value an earlier block of the same fork gave them (zero before that).

Each block's PackBits bytes are expanded on their own (TN1023): a control byte `n` (signed)
copies `n + 1` literal bytes for `n ≥ 0`, repeats the next byte `1 - n` times for `-127 ≤ n ≤ -1`,
and is skipped for `-128`. Output past the fork's declared length is dropped.

## 🔢 The fixed code

257 symbols; their code lengths, in symbol order, are these runs (count × length):

```
1×3, 1×4, 4×5, 12×6, 32×7, 16×8, 49×9, 2×10, 2×9, 40×10, 95×11, 2×13, 1×12
```

The lengths are a complete code (their Kraft sum is exactly 1). The codes are **not** canonical; they
are assigned in symbol order:

- symbol 0 gets the all-zero code;
- a symbol as long as the previous one gets the previous code plus one;
- a longer one gets the previous code plus one, shifted left by the length difference;
- a shorter one gets the previous code shifted right by the length difference, plus one.

So symbol 0 is `000`, 1 is `0010`, 2 is `00110`, 5 is `01001`, 6 is `010100`, 255 is
`1111111111101` and the end code 256 is `111111111111`.

## 🧪 Measured on the sample

| Entry | Fork | Blocks | Uncompressed |
|---|---|---|---|
| About System 7.5 | resource | one coded block, 148 table entries, `count` 1,671 | 2,336 |
| About System 7.5 | data | one coded block, 92 table entries, `count` 19,844 | 19,696 |
| SimpleText | resource | a PackBits-only block (32,847 bytes), then a coded block with 256 entries | 58,768 |

Every fork passes the CRC-16 StuffIt stored for it, and equals what StuffIt-Go extracts
(`StuffItFixedHuffmanTests`, which reads the sample from `CWB_STUFFIT_FHF_DIR`: it carries Apple
software and is not committed).
