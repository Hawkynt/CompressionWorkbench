# RAR 2.0 compression on disk

This is the stream format of RAR 2.x archives (unpack version 20, and 26 for files
larger than 2 GB). It was written down from reading unrar 7.0.9 (`unpack20.cpp`). That
is sourcing rung 2: the unRAR licence rules out taking its code, and libarchive has no
RAR 2.0 decoder. `Rar2Decoder` is implemented from this page and checked against
archives that WinRAR 2.90 wrote.

## Bits and Huffman codes

The packed data is read most significant bit first. A table of code lengths (0 means
unused) defines a canonical Huffman code. Codes are assigned in order of increasing
length, and within one length in order of increasing symbol.

## Tables

A table read starts at the current bit position, without alignment.

1. One bit: audio block (1) or LZ block (0).
2. One bit: keep the previous lengths (1) or clear them (0) before adding.
3. Audio blocks only: 2 bits give the channel count minus one (1-4 channels). The
   current channel is reset to 0 if it is no longer below the count.
4. 19 precode lengths of 4 bits each.
5. The lengths, decoded with the precode:
   - 298 + 48 + 28 lengths for LZ (main, distance and repeat-length codes).
   - 257 × channels lengths for audio.

The precode symbols are:

| Symbol | Meaning |
|---|---|
| 0-15 | length = (previous length at this position + symbol) & 15 |
| 16 | repeat the length before, 3 + bits(2) times; not allowed at position 0 |
| 17 | 3 + bits(3) zeros |
| 18 | 11 + bits(7) zeros |

The lengths are remembered for the next table read. They are cleared for a non-solid
file.

## LZ block symbols

| Symbol | Meaning |
|---|---|
| 0-255 | literal |
| 256 | repeat the last match (last length, last distance) |
| 257-260 | distance = old[(ptr − (n − 256)) & 3]; length from the repeat-length code |
| 261-268 | length 2; distance = short base + 1 + bits |
| 269 | read new tables |
| 270-297 | a new match (length symbol n − 270), then a distance symbol |

For 257-260, `n` is the symbol and the length is `L[k] + 2 + bits(LB[k])` for
repeat-length symbol `k`. Then add 1 if distance >= 0x101, another 1 if >= 0x2000,
and another 1 if >= 0x40000.

For 261-268, with `s = n − 261`, the short base is `{0,4,8,16,32,64,128,192}[s]` and
the extra bits are `{2,2,3,4,5,6,6,6}[s]`.

For 270-297, the length is `L[n − 270] + 3 + bits(LB[n − 270])`. The distance symbol
`d` gives distance `D[d] + 1 + bits(DB[d])`. Then add 1 to the length if
distance >= 0x2000, and another 1 if >= 0x40000.

Every match, including 256, records its distance in `old[ptr]`, advances
`ptr = (ptr + 1) & 3`, and becomes the "last" length and distance.

`L` = `{0,1,2,3,4,5,6,7,8,10,12,14,16,20,24,28,32,40,48,56,64,80,96,112,128,160,192,224}`,
`LB` = `{0,0,0,0,0,0,0,0,1,1,1,1,2,2,2,2,3,3,3,3,4,4,4,4,5,5,5,5}`.

`D` is 0, 1, 2, 3, 4, 6, 8, 12, … doubling every two entries up to 32768, 49152, then
65536 to 983040 in steps of 65536 (48 entries).

`DB` = `{0,0,0,0,1,1,2,2,…,15,15,16 × 12}`.

## Audio blocks

Each channel has its own 257-symbol code. Symbol 256 means "read new tables". Any other
symbol is a delta `δ` for the current channel, which then advances round-robin.

Per channel, the state is:

- weights K1-K5,
- deltas D1-D4,
- the last delta and the last byte,
- a byte counter,
- 11 error sums.

One value `CD`, the last channel delta, is shared by all channels.

To decode a byte:

1. Count the byte.
2. Shift the deltas: `D4 = D3`, `D3 = D2`, `D2 = lastDelta − D1`, `D1 = lastDelta`.
3. Predict `p = ((8·last + K1·D1 + K2·D2 + K3·D3 + K4·D4 + K5·CD) >> 3) & 0xFF`.
4. The byte is `(p − δ) & 0xFF`.
5. With `e = (signed δ) << 3`, add `|e|`, `|e − D1|`, `|e + D1|`, `|e − D2|`,
   `|e + D2|`, `|e − D3|`, `|e + D3|`, `|e − D4|`, `|e + D4|`, `|e − CD|` and `|e + CD|`
   to the 11 error sums.
6. Set `CD = lastDelta = (signed)(byte − last)` and `last = byte`.
7. Every 32 bytes, take the smallest error sum (the first wins ties) and clear all
   sums. Sum number 2k−1 decrements weight Kk if it is >= −16. Sum number 2k
   increments Kk if it is < 16. Sum 0 changes nothing.

## Files and solid archives

A file starts with a table read unless it continues a solid archive whose tables are
already read. Decoding stops once the file's size has been produced. If at least 5
bytes of packed data remain, the next symbol is decoded. When it is 269 (in audio mode,
256), new tables are read for the next solid file.

A non-solid file starts with these all cleared:

- the window,
- the old distances and `ptr`,
- the last length and distance,
- the audio state,
- the remembered lengths.
