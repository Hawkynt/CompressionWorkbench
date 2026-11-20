# paq8l archive format and model

paq8l is a context-mixing archiver by Matt Mahoney and others (2007). Its output is defined
by a predictor and an arithmetic coder, not by a bitstream: a decoder must reproduce every
prediction of the encoder bit for bit. This page specifies that predictor, the coder and the
container closely enough to write a compatible implementation. `FileFormat.Paq8l` is written
from it.

**Sources (rung 2).** The GPL-2.0-or-later `paq8l.cpp` (release of 8 March 2007, source
update of 15 April 2007, from `http://mattmahoney.net/dc/paq8l.zip`, SHA-256
`fb72db122f89faedcc729fe1e3324cac57c9c48903e72107c8350cdbe5e374cf`) was read to write this
page. No code from it is used. Tables and arbitrary constants (the state table, the squash
table, hash multipliers, learning rates) are copied exactly, because they are the
specification. Behaviour was confirmed against two oracles that agree byte for byte with
each other: `paq8l.exe` from that archive under wine, and the same source built for Linux
with its portable C fallback (`-DUNIX -DNOASM`).

Notation: integers are 32-bit two's complement and wrap on overflow; `u32` marks unsigned
32-bit arithmetic. `>>` on a signed value is arithmetic. A shift count is taken modulo 32,
as on x86. `a & b`, `a | b` and `a ^ b` are bitwise. Integer division truncates toward
zero.

## 1. Container

```
"paq8l -" L CR LF                  L = level digit '0'..'9'
( size TAB name CR LF )*           size in decimal; name uses '/' separators
SUB (0x1A)
coded data                         one coder stream for all files, in header order
```

A reader takes the level from the eighth byte; a value outside 0–9 means level 5. Files are
coded back to back in header order through one coder and one model, whose state carries
from file to file. `MEM = 65536 << level`. Level 0 stores bytes without the coder (§3).

## 2. Blocks and transforms

The bytes fed to the coder are not the files themselves. Each file is cut into chunks of at
most `MEM × 64` bytes. Each chunk is segmented into typed blocks:

```
type (1 byte)   size (4 bytes, big-endian)   payload
```

* type 0, default: the payload is `size` bytes, verbatim.
* type 1, JPEG: the same as type 0.
* type 2, EXE: the payload is `size` (4 bytes, big-endian), `begin` (4 bytes, big-endian),
  then `size` transformed bytes. `begin` is the offset of the block within its file (not
  within its chunk).

A decoder reads a block header whenever its previous block is used up. A header whose size
decodes as negative is treated as size 1. Blocks are written with the transform test of
§2.3. At every level, level 0 included, the coder carries this block stream, not the raw
files. Block state persists across chunk and file
boundaries.

### 2.1 Segmentation

The chunk is scanned from its start, with the current type initially 0. A scan with current
type T walks bytes `i = 0, 1, …` of what remains. Two 32-bit shift registers hold the last
eight bytes: `b0` holds the last four, and `b1` holds the four before them. The scan keeps
this state:

* JPEG: `soi`, `sof` and `sos`, the positions of the last such markers, all 0 at the start;
* EXE: `abs[256]` and `rel[256]`, all 0 at the start; a run counter `run`; `first`, the
  start of the run, and `last`, the most recent hit.

After shifting in byte `i`:

1. If `i ≥ 3` and `b0 & 0xFFFFFFF0 == 0xFFD8FFE0`, set `soi = i`.
2. If `soi ≠ 0`, `i − soi < 0x10000`, `b1 & 0xFF == 0xFF` and `b0 & 0xFF0000FF == 0xC0000008`,
   set `sof = i`.
3. If `soi ≠ 0`, `sof ≠ 0`, `sof > soi`, `i − soi < 0x10000`, `i − sof < 0x1000` and
   `b0 & 0xFFFF == 0xFFDA`, set `sos = i`. Then, if T ≠ JPEG, the scan ends: a JPEG block
   starts at `soi − 3`.
4. If T = JPEG, `sos ≠ 0`, `i > sos`, `b0 & 0xFF00 == 0xFF00`, `b0 & 0xFF ≠ 0` and
   `b0 & 0xF8 ≠ 0xD0`, the scan ends: a default block starts after byte `i`.
5. If `b1 & 0xFE == 0xE8` and `(b0 + 1) & 0xFE == 0` (the byte four back is E8 or E9, and
   the current byte is 00 or FF), work with:
   * `r = b0 >> 24` (`u32`), the low byte of the relative address;
   * `a = (r + i) & 0xFF`;
   * `dr = i − rel[r]` and `da = i − abs[a]`.

   If `da < dr`, `da < 0x1000` and `abs[a] > 5`, then:
   * `last = i`;
   * `run` increases by one;
   * if `first = 0` or `first > abs[a]`, set `first = abs[a]`.

   Otherwise `run = 0`.

   Then, if T ≠ EXE, `run ≥ 4` and `first > 5`, the scan ends: an EXE block starts at
   `first − 5`. Finally, set `abs[a] = i` and `rel[r] = i`.
6. If T = EXE and `i − last > 0x1000`, the scan ends: a default block starts at `last`.

When the scan ends, the bytes from the current block start to the end position form a block
of type T. A block of length 0 is not written. The scan then resumes at the end position with
the new type, and with all JPEG and EXE state reset. A scan that reaches the end of the chunk
writes the rest as one block of type T.

### 2.2 EXE transform

The payload is processed in 64 KiB sub-blocks at offsets `o = 0, 65536, …`. Within a
sub-block of length `n`, positions `i` are visited from `n − 1` down to 4. A position
qualifies when byte `i − 4` is E8 or E9 and byte `i` is 00 or FF. For each one:

* read the four bytes `i−3 … i` as a little-endian value `v`;
* compute `v + o + begin + i + 1`;
* sign-extend the result from bit 24 (shift left 7, then arithmetic shift right 7);
* write it back to the same four bytes, little-endian.

The decoder keeps a five-byte window and the offset of the newest byte, counted from 1. It
reads each block's header first. Each output step fills the window up to five bytes and
checks three conditions:

* the oldest byte is E8 or E9;
* the newest byte is 00 or FF;
* `((offset − 1) ^ (offset − 5)) & −65536 = 0`.

When all three hold, it reads the four newest bytes as a little-endian value, subtracts
`offset + begin`, sign-extends from bit 24, and writes the value back. Either way, it then
emits the oldest byte.

Decoder state persists until the block's bytes are used up and the window is empty.

### 2.3 Verification and fallback

After a chunk is segmented and transformed, the encoder decodes the result with a decoder
whose state persists across chunks (and is never reset). It accepts the transform only when
the decoded chunk equals the input and the transformed data is fully consumed. Otherwise it
codes the chunk as one type-0 block holding the original bytes. A compatible writer must keep
this test and its persistent decoder state, because the fallback changes the output.

## 3. Arithmetic coder

The coder keeps `x1 = 0`, `x2 = 0xFFFFFFFF` (`u32`), and in decoding `x`, the first four
coded bytes big-endian. For each bit, with prediction `p` (0–4095, the probability of a 1):

```
p' = p + (p < 2048)
xmid = x1 + (x2 − x1 >> 12) × p' + ((x2 − x1 & 0xFFF) × p' >> 12)      (u32)
bit = 1: x2 = xmid      bit = 0: x1 = xmid + 1                         (decoding: bit = x ≤ xmid)
```

The predictor is updated with the bit before the coder renormalises. While
`(x1 ^ x2) & 0xFF000000 = 0`, the encoder emits `x2 >> 24`; then `x1 <<= 8` and
`x2 = (x2 << 8) | 255`. The decoder instead shifts the next coded byte (0 past the end)
into `x`, taking 255 for every byte past the end of the archive. At the end the encoder
emits `x1 >> 24`.

A byte is coded most significant bit first. At level 0, bytes are written and read as they
are, with no model.

## 4. Primitives

### 4.1 squash, stretch, ilog

`squash(d)` returns 4095 for `d > 2047` and 0 for `d < −2047`. Otherwise it interpolates
the table

```
1,2,3,6,10,16,27,45,73,120,194,310,488,747,1101,1546,2047,2549,2994,3348,3607,
3785,3901,3975,4022,4050,4068,4079,4085,4089,4092,4093,4094
```

as `(T[k] × (128 − w) + T[k+1] × w + 64) >> 7`, with `w = d & 127` and `k = (d >> 7) + 16`.

`stretch(p)` for p in 0–4095 inverts squash. Walk `x` from −2047 to 2047, with `pi` starting
at 0: for each `x`, every `j` from `pi` to `squash(x)` gets `stretch(j) = x`, then
`pi = squash(x) + 1`. Finally `stretch(4095) = 2047`.

`ilog(x)` for x in 0–65535 is a table: `ilog(0) = ilog(1) = 0`. Start with `acc = 14155776`;
for `i = 2 … 65535`, `acc += 774541002 / (2i − 1)` (`u32`), and `ilog(i) = acc >> 24`.
`llog(x)` (`u32`) is `256 + ilog(x >> 16)` for `x ≥ 2^24`, `128 + ilog(x >> 8)` for
`x ≥ 2^16`, and `ilog(x)` below that.

### 4.2 Bit-history states

A bit history is one byte. The transition table maps (state, bit) to the next state, and
records each state's zero and one counts `n0` and `n1`. The full table (253 states) is a
constant of the format, reproduced in the implementation. State 0 is "nothing seen".

### 4.3 hash

`hash(a, b, c = 0xFFFFFFFF, d = 0xFFFFFFFF, e = 0xFFFFFFFF)` (`u32`):

```
h = a×200002979 + b×30005491 + c×50004239 + d×70004807 + e×110002499
hash = h ^ h>>9 ^ a>>2 ^ b>>3 ^ c>>4 ^ d>>5 ^ e>>6
```

### 4.4 Random numbers

A lagged generator over 64 `u32` words:
* `R[0] = 123456789` and `R[1] = 987654321`;
* `R[j+2] = R[j+1]×11 + R[j]×23/16` (`u32`, the division applied to the product), for
  j = 0–61;
* a counter `k` starts at 0.

Each draw increments `k`, sets `R[k & 63] = R[(k − 24) & 63] ^ R[(k − 55) & 63]`, and
returns that value. One generator is shared by the whole model (§5.3).

### 4.5 Adaptive state map

A table of 256 16-bit probabilities. Entry `s` starts at `65536 × (n1 + 1) / (n0 + n1 + 2)`,
where n0 and n1 are the state's counts, except:
* if `n0 = 0`, n1 is first multiplied by 64;
* if `n1 = 0`, n0 is first multiplied by 64.

A lookup with state `s`:
1. updates the entry used by the previous lookup: `t += ((bit << 16) − t + 128) >> 8`,
   truncated to 16 bits, where `bit` is the most recent coded bit;
2. remembers `s`;
3. returns `t[s] >> 4`.

The first remembered entry is 0.

### 4.6 APM (adaptive probability map)

An APM with N contexts has `33 × N` 16-bit entries. Every context's row starts as
`squash((j − 16) × 128) × 16` for j = 0–32. A refinement `apm(p, cx, rate = 7)` works in
four steps:

1. Compute `g = (bit << 16) + (bit << rate) − 2·bit`.
2. Update the two entries at the remembered index `k` and `k + 1`: each
   `t += (g − t) >> rate`, truncated to 16 bits. The first remembered index is 0.
3. With `s = stretch(p)` and `w = s & 127`, remember `k = ((s + 2048) >> 7) + 33·cx`.
4. Return `(t[k] × (128 − w) + t[k+1] × w) >> 11`.

### 4.7 Mixer

A mixer has `N` inputs (rounded up to a multiple of 8), `M` weight sets and up to `S`
selected sets. Inputs are 16-bit; weights are 16-bit and start at a given value.

Each selection call `select(cx, range)` adds `base + cx` to the selected list and advances
`base` by `range`.

Output:
1. Pad the inputs with zeros to a multiple of 8.
2. For each selected set `c`, compute the dot product: the sum over input pairs
   `(i, i+1)` of `(x_i·w_ci + x_{i+1}·w_c,i+1) >> 8`.
3. With one set (S = 1), the result is `squash(dot >> 8)`, stored as the set's output.
4. With several sets:
   * first train the second-stage mixer on its previous output;
   * set `out_c = squash(dot_c >> 5)` for each set;
   * feed `stretch(out_c)` to a second-stage mixer with S inputs, one weight set, and
     weights starting at 32767;
   * select its only set, and return its output.

Training, once per bit, before new inputs: for each selected set `c`, compute
`err = ((bit << 12) − out_c) × 7`. Each weight `i` (padded inputs included) becomes
`clamp(w + (((x_i × err × 2) >> 16) + 1 >> 1), −32768, 32767)`. Then the input count, base
and selection are cleared. Before the first prediction, `out_c` is 2048.

## 5. Context maps

### 5.1 Bucket table (with B-byte elements)

A table of `n` elements of B bytes (n a power of 2). Bytes 0–1 of an element are a 16-bit
checksum, and byte 2 is its priority (0 = empty). A lookup with a `u32` context `i`:

1. Compute `chk = ((i >> 16) ^ i) & 0xFFFF`, then `i = (i × 8) & (n − 1)`.
2. Probe up to eight elements `i … i+7`. An element whose priority byte is 0 takes the
   checksum. The first element whose checksum equals `chk` is a match.
3. On a match at probe 0, return that element.
4. Otherwise, take the element to move forward:
   * a match at probe j is moved;
   * with no match, a fresh element is used: zeroed, with the checksum set, and j = 7. If
     element 7's priority is above element 6's, j = 6 instead.
5. Shift elements `i … i+j−1` up by one, put the moved element at `i`, and return it.

The caller sees bytes 1 … B−1 of the element (its "byte 0" is the checksum's high byte).

### 5.2 Run map

A run map is a bucket table with `m/4` elements of 4 bytes. It works on the visible bytes
`[1]` (count) and `[2]` (value) of the current element. Initially the current element is
element 0.

Once per byte, with the new context:
1. If the current count is 0, or the current value differs from the last byte, set the count
   to 1 and the value to the last byte; otherwise, if the count is below 255, increment it.
2. Look up the new context; that element becomes current.

Every bit, the map adds one input:
* if `(value + 256) >> (8 − bpos)` equals the partial byte `c0`, add
  `(bit (7 − bpos) of value ? 1 : −1) × ilog(count + 1) × 8`;
* otherwise add 0.

It reports whether the count is non-zero.

### 5.3 Direct map

A direct map has `m/2` 16-bit probabilities, all starting at 32768. Setting a context selects
the row `(cx × 256) & (m/2 − 256)`. Each bit:
1. update the previously used entry: `t += ((bit << 16) − t + (1 << (rate − 1))) >> rate`,
   with rate 7;
2. use the entry `row + c0`;
3. add the input `stretch(t >> 4)`.

The first previously used entry is entry 0.

### 5.4 Nibble context map (C contexts, m bytes)

This is the main model. Memory is an array of `m/64` slots of 64 bytes:
* bytes 0–13: seven 16-bit checksums;
* byte 14: a recency byte (the low and high nibbles name the last two slots used);
* bytes 15–63: seven 7-byte histories.

In a history, byte 0 is the state after the first bit, bytes 1–2 are the states after the
second bit, and bytes 3–6 are the states after the third bit. Byte 0 also serves as the
history's priority.

A slot lookup with checksum `ch` returns a history in four steps:
1. If the history named by the low recency nibble matches `ch`, return it.
2. Otherwise search the seven histories in order. The first match `i` is returned, and the
   recency byte becomes `(old << 4) | i`.
3. With no match, take the history with the lowest priority that neither recency nibble names
   (the first such history on a tie). Starting from `bi = 0` and best = 0xFFFF, a history
   qualifies only when its priority is strictly below best. In step 2 the new recency byte
   is truncated to 8 bits.
4. Set the recency byte to `0xF0 | bi`, set the checksum, zero the history's seven bytes, and
   return it.

**Setting contexts.** At each byte boundary the caller sets up to C contexts, in order, as
`u32` values. For context number `k`:
1. `x = cx × 987654323 + k`;
2. `x = (x << 16) | (x >> 16)`;
3. store `x × 123456791 + k`.

**Per bit**, for each set context `k` (in order), the map has a current history byte pointer
(possibly none), a history base and a run pointer. The update runs in four steps.

1. **Update.** If the pointer is set, the state there becomes `next(state, bit)`. If the new
   state is ≥ 204 and `draw() << ((452 − state) >> 3)` is non-zero, subtract 4. The draw is
   made only when the state is ≥ 204.
2. **Move the pointer:**
   * if `bpos > 1` and the run byte `run[0]` is 0, the pointer is cleared;
   * for `bpos` 1, 3 or 6, it points to `base + 1 + (c0 & 1)`;
   * for `bpos` 4 or 7, it points to `base + 3 + (c0 & 3)`;
   * otherwise (`bpos` 0, 2 or 5), base and pointer become the history returned by looking up
     slot `(ctx + c0) & (slots − 1)` with checksum `ctx >> 16`. When `bpos` is 0, it then:
     - **writes pending histories**: if the new base's byte 3 equals 2, the bit histories for
       the rest of the remembered byte `c = base[4] + 256` are written directly. Slot
       `(ctx + (c >> 6)) & mask` (same checksum) gets `[0] = 1 + (c>>5 & 1)`,
       `[1 + (c>>5 & 1)] = 1 + (c>>4 & 1)` and `[3 + (c>>4 & 3)] = 1 + (c>>3 & 1)`. Slot
       `(ctx + (c >> 3)) & mask` gets `[0] = 1 + (c>>2 & 1)`,
       `[1 + (c>>2 & 1)] = 1 + (c>>1 & 1)` and `[3 + (c>>1 & 3)] = 1 + (c & 1)`. Then the
       base's byte 6 becomes 0;
     - **updates the previous run** (`run` still points at the previous byte's bytes 3–4) with
       the last byte `c1`:
       - if `run[0] = 0`, set `run = (2, c1)`;
       - else if `run[1] ≠ c1`, set `run = (1, c1)`;
       - else if `run[0] < 254`, add 2;
       - else if `run[0] = 255`, set it to 128;
     - points the run pointer at `base + 3`.
3. **Run input.** With `rc = run[0]`: if `(run[1] + 256) >> (8 − bpos) = c0`, add
   `(bit (7 − bpos) of run[1] ? 1 : −1) × (ilog(rc + 1) << (2 + (~rc & 1)))`; otherwise add 0.
4. **History inputs.** With `s` the state at the pointer (0 when there is none), consult the
   context's state map to get `p1 = sm(s)`, then add five inputs (§5.5). The context counts
   as found when `s > 0`.

After the last bit of a byte (`bpos = 7` on entry), the context count resets. The map returns
the number of found contexts.

Initially every pointer, base and run pointer refers to the first history of slot 0 (with the
run pointer at its byte 3).

### 5.5 Five inputs from a state

With `p1 = sm(s)` (12-bit) and `st = stretch(p1) >> 2`, let `z0 = −1` when `n0(s) = 0` and 0
otherwise, and `z1 = −1` when `n1(s) = 0` and 0 otherwise. Then, with `p1' = p1 >> 4` and
`p0' = 255 − p1'`, the five inputs are:

* `st`;
* `p1' − p0'`;
* `st × (z1 − z0)`;
* `(p1' & z0) − (p0' & z1)`;
* `(p1' & z1) − (p0' & z0)`.

## 6. Models

Global state per bit, updated before the models run:

* `bit`, the last coded bit;
* `c0`, the partial byte with a leading 1;
* `c4`, the last four bytes, newest in the low byte;
* `bpos`, the number of bits already coded in the current byte (0–7);
* `pos`, the number of whole bytes so far;
* a history buffer of `MEM × 8` bytes, indexed modulo its size. `B(i)` is the byte `i`
  positions back: `B(1)` is the last whole byte.

When a byte completes, it is stored at `pos`, `pos` increments, `c4` takes it in, and `c0`
resets to 1. All model state is created zeroed, unless stated otherwise.

The orchestrator (§6.12) calls the models in a fixed order. Each model adds its inputs to
the main mixer in that order.

### 6.1 Match model

The model keeps a table of `MEM` ints, a hash `h`, a match pointer `ptr`, a length `len`, and
a direct map of 0x20000 bytes.

At `bpos = 0`:
1. `h = (h × 997 × 8 + B(1) + 1) & (MEM − 1)`.
2. If `len > 0`, increment both `len` and `ptr`. Otherwise:
   * set `ptr = table[h]`;
   * if `ptr ≠ 0` and `pos − ptr` is less than the buffer size, extend `len` while
     `B(len + 1)` equals the buffered byte at `ptr − len − 1` and `len < 65534`.
3. Set `table[h] = pos`.
4. The model's result is `len`.
5. Set the direct map's context to `pos`.

Every bit:
1. Clamp `len` to 65534.
2. Check three conditions:
   * `len > 0`;
   * `B(1)` equals the buffered byte at `ptr − 1`;
   * `(buf[ptr] + 256) >> (8 − bpos) = c0`.

   If all three hold, `sign = ±1` by bit `7 − bpos` of `buf[ptr]`. Otherwise `sign = 0` and
   `len = 0`.
3. Add `sign × 4 × ilog(len)` and `sign × 64 × min(len, 32)`.
4. Mix the direct map.

### 6.2 Fax model (two-level images, 1728 pixels per row)

Three state bytes are indexed by contexts in a table of 0x10200 bytes. There are three state
maps and four row registers `r0`–`r3`, all `u32`.

Every bit:
1. Advance the states at the three previous contexts with `bit`.
2. Shift in one bit to each register:
   * `r0 = 2·r0 + bit`;
   * `r1 = 2·r1 + (B(215) >> (7−bpos) & 1)`;
   * `r2 = 2·r2 + (B(431) >> (7−bpos) & 1)`;
   * `r3 = 2·r3 + (B(647) >> (7−bpos) & 1)`.
3. Compute the three contexts:
   * `cx0 = (r0 & 7) | (r1 >> 4 & 0x38) | (r2 >> 3 & 0xC0)`;
   * `cx1 = 0x100 + ((r0 & 1) | (r1 >> 4 & 0x3E) | (r2 >> 2 & 0x40) | (r3 >> 1 & 0x80))`;
   * `cx2 = 0x200 + ((r0 & 0x3F) ^ (r1 & 0x3FFE) ^ (r2 << 2 & 0x7F00) ^ (r3 << 5 & 0xF800))`.
4. Add `stretch(sm_k(state[cx_k]))` for k = 0, 1, 2.

### 6.3 Word model

The model has a nibble map of `MEM × 16` bytes with 20 contexts. Its state:
* word hashes `w0`–`w5`;
* a text hash `t0`;
* newline positions `nl1 = −3` and `nl = −2`.

At `bpos = 0`, with `c` the last byte (case-folded A–Z to a–z):
1. **Words.** If `c` is a–z or ≥ 128, extend the current word: `w0 = w0 × 263 × 32 + c` and
   `t0 = t0 × 997 × 16 + c`. Otherwise, if `w0 ≠ 0`, shift the words:
   `w5 = w4×23, w4 = w3×19, w3 = w2×17, w2 = w1×13, w1 = w0×11, w0 = 0`.
2. **Columns.** If `c = 10`, set `nl1 = nl` and `nl = pos − 1`. Then
   `col = min(255, pos − nl)` and `above = buf[nl1 + col]`.
3. **Contexts.** With `h = w0 × 271 + B(1)` (`u32`), set these 20 contexts in this order:
   * `h`, `w0`, `h + w1`, `w0 + w1×31`, `h + w1 + w2×29`;
   * `t0 & 0xFFFFFF`, `t0 & 0xFFFFF`;
   * `h + w2`, `h + w3`, `h + w4`, `h + w5`;
   * `B(1) | B(3)<<8 | B(5)<<16`, `B(2) | B(4)<<8 | B(6)<<16`;
   * `h + w1 + w3`, `h + w2 + w3`;
   * `col<<16 | B(1)<<8 | above`, `B(1)<<8 | above`, `col<<8 | B(1)`, `col`.

Every bit, mix the map.

### 6.4 Record model

The model's state:
* position tables `p1`–`p4` (256 entries each) and `wpos` (65536 entries);
* `rlen = 2`, with candidates `rlen1 = 3` and `rlen2 = 4`, and counts `rc1 = rc2 = 0`;
* four nibble maps, each with 3 contexts: A of 32768 bytes, B of 16384, C of 65536 and D of
  `MEM`.

At `bpos = 0`, with `w = c4 & 0xFFFF`, `c = w & 255` and `d = w >> 8`:

1. **Record length.** Let `r = pos − p1[c]`. A candidate is found when:
   * `r > 1`;
   * `r = p1[c] − p2[c] = p2[c] − p3[c] = p3[c] − p4[c]`;
   * and either `r > 15`, or both `c = B(5r + 1)` and `c = B(6r + 1)`.

   For a candidate:
   * if `r = rlen1`, increment `rc1`;
   * else if `r = rlen2`, increment `rc2`;
   * else if `rc1 > rc2`, set `rlen2 = r` and `rc2 = 1`;
   * else set `rlen1 = r` and `rc1 = 1`.

   Then:
   * if `rc1 > 15` and `rlen ≠ rlen1`, set `rlen = rlen1` and `rc1 = rc2 = 0`;
   * then, if `rc2 > 15` and `rlen ≠ rlen2`, set `rlen = rlen2` and `rc1 = rc2 = 0`.
2. **Contexts:**
   * A: `c<<8 | min(255, pos − p1[c]) / 4`, `w<<9 | llog(pos − wpos[w]) >> 2`,
     `rlen | B(rlen)<<10 | B(2·rlen)<<18`;
   * B: `w | rlen<<8`, `d | rlen<<16`, `c | rlen<<8`;
   * C: `B(1)<<8 | min(255, pos − p1[B(1)])`, `B(1)<<17 | B(2)<<9 | llog(pos − wpos[w]) >> 2`,
     `B(1)<<8 | B(rlen)`;
   * D, with `col = pos mod rlen`: `rlen | B(rlen)<<10 | col<<18`,
     `rlen | B(1)<<10 | col<<18`, `col | rlen<<12`.
3. **Positions:** shift `p4 = p3`, `p3 = p2`, `p2 = p1` at `c`, then set `p1[c] = pos` and
   `wpos[w] = pos`.

Every bit, mix A, B, C, D in order.

### 6.5 Sparse model

The model has one nibble map of `MEM × 2` bytes with 48 contexts and a `mask`. Its inputs are
the match length class `m` and the order-model hit count `o` (§6.12). At `bpos = 0` it sets,
in order:

1. `c4 & 0x00F0F0F0`, `(c4 & 0xF0F0F0F0) + 1`, `(c4 & 0x00F8F8F8) + 2`,
   `(c4 & 0xF8F8F8F8) + 3`, `(c4 & 0x00E0E0E0) + 4`, `(c4 & 0xE0E0E0E0) + 5`,
   `(c4 & 0x00F0F0FF) + 6`;
2. `m`, `o`, `c4 & 0x00FF00FF`, `c4 & 0xFF0000FF`, `B(1) | B(5)<<8`, `B(1) | B(6)<<8`,
   `B(3) | B(6)<<8`, `B(4) | B(8)<<8`;
3. for `i = 1 … 7`: `B(i+1)<<8 | B(i+2)`, `B(i+1)<<8 | B(i+3)`, `m | B(i)<<8`;
4. A class flag `f` of the last byte, then the mask contexts:
   * `f = 0` unless the last byte is odd;
   * for an odd last byte `c`: `f = 1` if `c` is a C-locale letter, else `f = 2` if it is
     C-locale punctuation, else `f = 3` if it is C-locale white space, else `f = 4` (bytes
     ≥ 128 are none of these). This is the original's behaviour, a precedence slip: its
     intended 3-bit classes 5–7 are never reached;
   * `mask = (mask << 3) | f`, then the contexts `mask`, `mask<<8 | B(1)`,
     `mask<<17 | B(2)<<8 | B(3)` and `(mask & 0x1FF) | ((c4 & 0xF0F0F0F0) << 9)`.

Every bit, mix the map.

### 6.6 Distance model

The model has one nibble map of `MEM` bytes with 3 contexts, and three positions `q0`, `q20`
and `qnl`, all starting at 0. At `bpos = 0`, with `c` the last byte:

* if `c = 0`, set `q0 = pos`;
* if `c = 0x20`, set `q20 = pos`;
* if `c` is 0xFF, CR or LF, set `qnl = pos`.

The contexts are then:
* `min(pos − q0, 255) | c<<8`;
* `min(pos − q20, 255) | c<<8`;
* `min(pos − qnl, 255) | ((c<<8) + 234567)`.

Every bit, mix the map.

### 6.7 Image model (24-bit BMP and TIFF)

The model's state:
* `w`, the row width in bytes, and `eoi`, the end of the image;
* `tiff`, the position of a TIFF header;
* six direct maps, five of 0x20000 bytes and one of 0x40000;
* a nibble map of `MEM × 4` bytes with 8 contexts.

`I4(i)` is the little-endian value at `B(i)` (low) to `B(i−3)` (high), and `I2(i)` is
`B(i) + 256·B(i−1)`.

At `bpos = 0`:
1. **BMP.** If `B(54) = 'B'`, `B(53) = 'M'`, `I4(44) = 54`, `I4(40) = 40` and `I4(24) = 0`,
   then:
   * `w = ((I4(36) + 3) & −4) × 3` and `height = I4(32)`;
   * if `w < 0x30000` and `height < 0x10000`, then `eoi = pos + w × height`; otherwise
     `eoi = pos`.
2. **TIFF:**
   * if `c4 = 0x49492A00`, set `tiff = pos`;
   * if `pos − tiff = 4` and `c4 ≠ 0x08000000`, set `tiff = 0`;
   * if `tiff ≠ 0` and `pos − tiff = 200`, read the directory:
     - `n = I2(pos − tiff − 4)`, `w = 0`, and width, height, compression and bpp start at 0;
     - for `i = tiff + 6`, stepping by 12, while `i < pos − 12` and `−−n > 0`: read
       `tag = I2(pos − i)`, `fmt = I2(pos − i − 2)`, `len = I4(pos − i − 4)` and
       `val = I4(pos − i − 8)`;
     - if `fmt` is 3 or 4 and `len = 1`, then tag 256 gives the width, 257 the height,
       259 the compression and 277 the bpp;
     - if width > 0, height > 0, width × height > 50, compression = 1 and bpp is 1 or 3, set
       `eoi = tiff + width × height × bpp` and `w = width × bpp`;
     - if `eoi ≤ pos`, set `tiff = w = 0`.
3. If `pos > eoi`, then `w = 0` and the model adds nothing and returns 0. This check runs
   every bit. Because `eoi` starts at 0, the model is active (with `w = 0`) for the bits of
   the very first byte: there its direct maps add inputs with their initial contexts.
4. **Contexts**, with `color = pos mod 3`:
   * `mean = B(3) + B(w−3) + B(w) + B(w+3)`;
   * `var = (B(3)² + B(w−3)² + B(w)² + B(w+3)² − mean²/4) >> 2`, then `mean >>= 2`;
   * `lv = ilog(var)`.

   The nibble map gets these hashes, with a counter `i` running from 1 to 8:
   * `hash(i, B(3)>>2, B(w)>>2, color)`;
   * `hash(i, B(3)>>2, B(1)>>2, color)`;
   * `hash(i, B(3)>>2, B(2)>>2, color)`;
   * `hash(i, B(w)>>2, B(1)>>2, color)`;
   * `hash(i, B(w)>>2, B(2)>>2, color)`;
   * `hash(i, (B(3)+B(w))>>1, color)`;
   * `hash(i, (B(3)+B(w))>>3, B(1)>>5, B(2)>>5, color)`;
   * `hash(i, mean, lv>>5, color)`.

   The direct maps get, in order:
   * `(B(3)+B(w))>>1`;
   * `(B(3)+B(w)−B(w+3))>>1`;
   * `(2·B(3)−B(6))>>1`;
   * `(2·B(w)−B(2w))>>1`;
   * `(B(3)+B(w)−B(w−3))>>1`;
   * `mean>>1 | (lv<<1 & 0x180)`.

Every bit (while active), mix the six direct maps and then the nibble map, and return `w`.

### 6.8 JPEG model

This model decodes baseline Huffman JPEG well enough to know which code is being coded. It
runs a small model of its own and contributes one input.

**State:**
* `jpeg` (0, 1 = header seen, 2 = in scan data) and `next`, the value returned;
* a skip counter `app`;
* positions `sof`, `sos` and `data`;
* up to 8 Huffman table positions;
* the Huffman state:
  - `code`, `bits`, `size` and `rs = −1`;
  - `mcupos` and `mcusize`;
  - two selector arrays `sel[2][10]`;
  - `color[10]`;
  - `pred[4]`, the DC predictors;
  - `dc`, `width`, `row` and `column`;
  - a 0x20000-byte coefficient buffer with position `cpos`;
  - the last four code hashes `h1`–`h4` and RS values `rs1`–`rs4`;
  - the S sums `ss` and `ss1`–`ss4`;
* the decoding tables: 128 entries of (min, max, val) and a 2048-byte value buffer.

**Per bit**, in order:

1. At `bpos = 0`, set `next = (jpeg > 1)`. If `bpos ≠ 0` and `jpeg = 0`, return `next`. At
   `bpos = 0`, if `app > 0`, decrement it. While `app > 0`, return `next`.
2. At `bpos = 0`:
   * **SOI.** If `jpeg = 0`, `B(4) = FF`, `B(3) = D8`, `B(2) = FF` and `B(1) >> 4 = E`, then
     `jpeg = 1`. Clear:
     - `app`, `sos`, `sof`, the table count, `data`, `mcusize` and the image width in MCUs;
     - the Huffman state, with `rs = −1`;
     - the decoding tables and `pred`.
   * **End.** If `jpeg ≠ 0`, `data ≠ 0`, `B(2) = FF`, `B(1) ≠ 0` and `B(1) & F8 ≠ D0`: when
     `B(1) ≠ D9`, give up (below); either way `jpeg = 0`.
   * If `jpeg = 0`, return `next`.
   * **APPx/COM.** If `data = 0`, `app = 0`, `B(4) = FF`, and `B(3) >> 4 = E` or `B(3) = FE`,
     set `app = B(2)·256 + B(1) + 2`.
   * **SOS.** If `B(5) = FF` and `B(4) = DA`, let `len = B(3)·256 + B(2)`. If
     `len = 6 + 2·B(1)` and `1 ≤ B(1) ≤ 4`, set `sos = pos − 5`, `data = sos + len + 2` and
     `jpeg = 2`.
   * **DHT.** If `B(4) = FF`, `B(3) = C4` and fewer than 8 tables are recorded, record
     `pos − 4`.
   * **SOF0.** If `B(4) = FF` and `B(3) = C0`, set `sof = pos − 4`.
   * **Restart.** If `B(2) = FF` and `B(1) & F8 = D0`, reset `code`, `bits`, `size` and
     `mcupos` to 0, set `rs = −1`, and zero `pred`.
3. **Tables.** At `pos = data` and `bpos = 1`, build the tables (layout below). Give up if
   none were recorded.
4. **Huffman step.** If `mcusize ≠ 0` and `B(1 + (bpos = 0)) ≠ FF` (stuffing skipped), give
   up if `bits > 32`. Otherwise run one decoding step with `bit` (below).
5. If `jpeg = 0` or `data = 0`, return `next`.
6. **Own model.** Run it and add `stretch(p)` to the main mixer. Return 1.

"Give up" sets `jpeg = 0` and returns `next`.

**Building the tables.** For each recorded table position `t`:
* let `p = t + 4` and `end = p + buf[p−2]·256 + buf[p−1] − 2`;
* read definitions while all of these hold: `p < end`, `end < pos`, `end < p + 2100`, and
  fewer than 9 definitions have been read for this table.

For each definition:
1. Let `tc = buf[p] >> 4` and `th = buf[p] & 15`. If `tc ≥ 2` or `th ≥ 4`, stop.
2. Copy 256 bytes from `buf[p+17]` into the value buffer at `tc·1024 + th·256`.
3. For lengths `j = 0 … 15`, set `min_j = code`, `code += L_j` (the byte `buf[p+1+j]`),
   `max_j = code`, and `val_j = h`, where `h` starts at `tc·1024 + th·256` and advances by
   `L_j` after each length. Then `code *= 2`.
4. `p` moves past the values. Give up if `h ≥ 2048`.

After the loop, give up unless `p = end`.

Then:
1. Reset `code`, `bits` and `size` to 0, and set `rs = −1`.
2. If `sof = 0` and `sos ≠ 0`, return `next` without building the MCU layout.
3. Let `ns = buf[sos+4]` and `nf = buf[sof+9]`. Give up if either is above 4.
4. For each scan component `i < ns`, and for each frame component `j < nf` whose id
   `buf[sof+3j+10]` equals `buf[sos+2i+5]`:
   * let `hv = buf[sof+3j+11]`, and track the largest horizontal factor `hmax = hv >> 4`;
   * the component has `blocks = (hv & 15) × (hv >> 4)` blocks. Give up unless
     `1 ≤ blocks` and `blocks + mcusize ≤ 10`;
   * for each block, set:
     - `sel[0][mcusize] = buf[sos+2i+6] >> 4`;
     - `sel[1][mcusize] = buf[sos+2i+6] & 15`;
     - `color[mcusize] = i`.

     Give up if a selector is 4 or more, then increment `mcusize`.
5. Give up unless `1 ≤ hmax ≤ 10`.
6. Set `width = (pixelWidth − 1) / (hmax·8) + 1` from the SOF width at `sof+7`, and give up
   unless it is positive.
7. Set `mcusize *= 64` and `row = column = 0`.

**Decoding step**, with the new bit:
1. Set `code = 2·code + bit` and increment `bits`.
2. **Find the symbol.** If `rs < 0`:
   * `ac = (mcupos & 63 > 0)` and `t = sel[ac][mcupos >> 6]`, with entry
     `e = tables[ac·64 + t·16 + bits − 1]`;
   * give up if `bits` is outside 1–16, `mcupos >> 6 ≥ 10`, `e.min > e.max` or
     `e.val ≥ 2048`;
   * if `code < e.max` (`u32`): give up if `code < e.min`; otherwise, with
     `k = e.val + code − e.min`, give up unless `0 ≤ k < 2048`, then set `rs = values[k]` and
     `size = bits`.
3. **Finish the symbol.** If `rs ≥ 0` and `size + (rs & 15) = bits`:
   * shift the history: `h4 = h3, h3 = h2, h2 = h1, h1 = hash(code, bits)`; the same for
     `rs1`–`rs4`, with `rs1 = rs`;
   * **AC** (`mcupos & 63 ≠ 0`):
     - **EOB** (`rs = 0`): round `mcupos` up to a multiple of 64 (give up unless it is within
       `mcusize` and 640), then write 0s to the coefficient buffer until `cpos` is a
       multiple of 64;
     - **otherwise:** with `r = rs >> 4` and `s = rs & 15` (give up if `s > 10`, or if
       `mcupos` and `mcupos + r` lie in different blocks), set `mcupos += r + 1`. Write
       `i<<4 | s` for `i = r … 1`, then `s<<4 | ((code << 2) >> s & 3) | 12`. Set `ss += s`.
     The extra-bits value is computed but unused.
   * **DC** (`mcupos & 63 = 0`):
     - give up if `rs ≥ 12`;
     - increment `mcupos`;
     - set `x = code & ((1<<rs) − 1)`; if `rs > 0` and `x >> (rs−1) = 0`, then
       `x −= (1<<rs) − 1`;
     - with `comp = color[mcupos >> 6]` (give up if it is outside 0–3), set
       `dc = pred[comp] += x`;
     - give up unless `cpos` is a multiple of 64, then write `(dc + 1023) >> 3`;
     - shift `ss4 = ss3, ss3 = ss2, ss2 = ss1, ss1 = ss` and set `ss = rs`.
   * Give up unless `0 ≤ mcupos ≤ mcusize`. When `mcupos ≥ mcusize`, set it to 0 and
     increment `column`. When `column` reaches `width`, wrap it to 0 and increment `row`.
   * Reset `code`, `size` and `bits` to 0, and set `rs = −1`.

Writes to the coefficient buffer go to `cpos & 0x1FFFF`, then increment `cpos`. Reads use
the same modulus.

**Own model.**

*Components:*
* a bucket table with 9-byte elements, `MEM` of them;
* 19 contexts with history pointers and 19 state maps;
* a mixer with 32 inputs, 800 sets and up to 4 selections;
* APMs of 1024 and 65536 contexts.

*Steps, each bit:*
1. If the last pointer is set, advance all 19 states with `bit`.
2. Train the mixer.
3. Compute:
   * `comp = color[mcupos >> 6]` and `coef = (mcupos & 63) | comp << 6`;
   * `hc = code | 1 << bits` (shift count modulo 32);
   * `zu` and `zv`, the zigzag coordinates of `mcupos & 63`.
4. A counter `hb` (starting at 2) is incremented and wraps to 0 when it exceeds 2 or when
   `bits = 0`.
5. When `hb = 0`, compute `mp = mcupos >> 4 | (mcupos < 64) << 7` and set 19 hashes, each
   led by its index `n = 1 … 19`:
   * `hash(n, hc, mcupos>>2, min(3, mcupos&63))`;
   * `hash(n, hc, mp>>4, cb(cpos−mcusize))`;
   * `hash(n, hc, mp>>4, cb(cpos−width·mcusize))`;
   * `hash(n, hc, ilog(ss3), coef)`;
   * `hash(n, hc, coef, column>>3)`;
   * `hash(n, hc, coef, column>>1)`;
   * `hash(n, hc, rs1, mp)`;
   * `hash(n, hc, rs1, rs2)`;
   * `hash(n, hc, rs1, rs2, rs3)`;
   * `hash(n, hc, ss>>4, mcupos)`;
   * `hash(n, hc, mp, cb(cpos−1))`;
   * `hash(n, hc, dc)`;
   * `hash(n, hc, rs1, coef)`;
   * `hash(n, hc, rs1, rs2, coef)`;
   * `hash(n, hc, mcupos>>3, ss3>>3)`;
   * `hash(n, hc, h1)`;
   * `hash(n, hc, coef, h1)`;
   * `hash(n, hc, zu, comp)`;
   * `hash(n, hc, zv, comp)`.
6. Add the input 128.
7. For each context, move its pointer:
   * `hb = 0`: the pointer becomes the priority byte (byte 2) of the bucket-table element
     for its hash, so the element's first state doubles as its priority;
   * `hb = 1`: advance it by `1 + (code & 1)·3`;
   * `hb = 2`: advance it by `1 + (code & 1)`.

   Then add `stretch(sm_k(*ptr))`.
8. Select `(0, 1)`, `(coef, 64)` and `(mcupos, 640)`.
9. Refine the mixer output `p`:
   * `p = apm1(p, hc & 1023)`;
   * `p = apm2(p, (hc & 255) | coef << 8)`.

The zigzag coordinates are constants of the format:

```
zu = 0,1,0,0,1,2,3,2,1,0,0,1,2,3,4,5,4,3,2,1,0,0,1,2,3,4,5,6,7,6,5,4,
     3,2,1,0,1,2,3,4,5,6,7,7,6,5,4,3,2,3,4,5,6,7,7,6,5,4,5,6,7,7,6,7
zv = 0,0,1,2,1,0,0,1,2,3,4,3,2,1,0,0,1,2,3,4,5,6,5,4,3,2,1,0,0,1,2,3,
     4,5,6,7,7,6,5,4,3,2,1,2,3,4,5,6,7,7,6,5,4,3,4,5,6,7,7,6,5,6,7,7
```

### 6.9 x86 model

The model has a nibble map of `MEM` bytes with 12 contexts, and runs only in EXE blocks. At
`bpos = 0`, context `i` (0–11) is built from:

* a prefix value:
  `prefix = [B(i+2) = 0F] + 2·[B(i+2) = 66] + 3·[B(i+2) = 67] + 4·[B(i+3) = 0F]
  + 8·[B(i+3) = 66] + 12·[B(i+3) = 67]`;
* `opcode = B(i+1)`;
* `modrm = B(i) & 0xC7` for `i > 0`, and 0 for `i = 0`.

The context is `prefix | opcode<<4 | modrm<<12 | x<<20`, where `x = B(1)` for `i > 4` and 0
otherwise.

### 6.10 Indirect model

The model has a nibble map of `MEM` bytes with 6 contexts, plus `u32 T1[256]` and
`u16 T2[65536]`.

At `bpos = 0`, with `d = c4 & 0xFFFF` and `c = d & 255`:
1. Set `T1[d >> 8] = T1[d >> 8] << 8 | c` and `T2[(c4 >> 8) & 0xFFFF] = (T2[…] << 8 | c)`
   (16 bits).
2. With `t = c | T1[c] << 8`, set the contexts `t & 0xFFFF`, `t & 0xFFFFFF`, `t` and
   `t & 0xFF00`.
3. With `t = d | T2[d] << 16`, set the contexts `t & 0xFFFFFF` and `t`.

### 6.11 DMC model

The model keeps a node graph of `2·MEM` nodes. Each node has two successors, a state byte,
and two 12-bit counts. It also has a state map, `top = 0`, `cur = 0` and `threshold = 256`.

Every bit, in order:
1. **Clone.** If `0 < top < 2·MEM`:
   * let `nx = succ(cur, bit)`, `n` the count of `bit` at `cur`, and `nn` the sum of `nx`'s
     two counts;
   * if `n ≥ 2·threshold` and `nn − n ≥ 3·threshold`, then:
     - with `r = n·4096/nn`, node `top` gets counts `count_nx·r >> 12` (each count truncated
       to 12 bits), and those amounts are subtracted from `nx`'s counts;
     - `top` copies `nx`'s successors and state;
     - `succ(cur, bit) = top` and `top` increments;
     - if `top = 2·MEM`, set `threshold = 512`.
2. **Reset.** If `top = 2·MEM` and `bpos = 1`, set `top = 0`.
3. **Rebuild.** If `top = 0`, rebuild nodes 0–65535 as an order-1 bytewise graph: for byte
   `j` and bit-tree node `i` (0–255), node `j·256 + i` gets:
   * successors `j·256 + 2i + 1` and `j·256 + 2i + 2` for `i < 127`, else `(i − 127)·256`
     and `(i + 1)·256`;
   * both counts 128.

   States are kept. Then set `top = 65536`, `cur = 0` and `threshold = 256`.
4. **Count.** At `cur`, if the count for `bit` is below 3800, add 256 to it. The state at
   `cur` becomes `next(state, bit)`, and `cur = succ(cur, bit)`.
5. **Inputs.** Add `stretch(sm(state_cur))` and
   `stretch((n1 + 5)·4096 / (n0 + n1 + 10))`, using `cur`'s counts.

### 6.12 Orchestration

The main mixer has 800 inputs, 3088 sets, up to 7 selections, and weights starting at 128.
Main components:
* a nibble map of `MEM × 32` bytes with 9 contexts;
* three run maps of `MEM` bytes;
* order hashes `X[0..15]`;
* the block type `ft` and a counter `left = 0`.

Each bit:

1. **Block header.** At `bpos = 0`, decrement `left`:
   * at −1, set `ft = B(1)`;
   * at −5, set `left` to the big-endian value of `B(4)…B(1)`, and add 8 if `ft = 2` (EXE).

   (This tracks the block headers of §2 in the coded stream.)
2. Train the mixer, then add the input 256.
3. Run the special models:
   * `isjpeg = JPEG model`;
   * `m = ilog(match model)`;
   * `isimg = image model`.
4. **JPEG.** If `isjpeg`, select `(1, 8)`, `(c0, 256)` and `(B(1), 256)`, and return the mixer
   output.
5. **Image.** Else if `isimg > 0`:
   * advance a counter (`col = col + 1`, wrapping to 0 at 24);
   * select `(2, 8)`, `(col, 24)`, `((B(isimg) + B(3)) >> 4, 32)` and `(c0, 256)`;
   * return the output.
6. **Order contexts.** At `bpos = 0`, for `k = 15 … 1`, set
   `X[k] = X[k−1]·257 + (c4 & 255) + 1`. Then set the contexts:
   * the 9-context map gets `X[0]` to `X[6]`;
   * run map 1 gets `X[7]`;
   * the 9-context map gets `X[8]`;
   * run map 2 gets `X[10]`;
   * run map 3 gets `X[12]`;
   * the 9-context map gets `X[14]`.
7. **Mix.**
   * `o` = the 9-context map's found count;
   * then run maps 1–3.
8. **Level ≥ 4 models**, in order:
   * sparse (with `m`, `o`);
   * distance;
   * fax;
   * record;
   * word;
   * indirect;
   * DMC;
   * x86, only when `ft = 2`.
9. **Selections**, with `o' = max(0, o − 2)` and `c1`–`c3 = B(1)`–`B(3)`:
   * `(c1 + 8, 264)`;
   * `(c0, 256)`;
   * `(o' + 8·(c4 >> 5 & 7) + 64·[c1 = c2] + 128·[ft = 2], 256)`;
   * `(c2, 256)`;
   * `(c3, 256)`;
   * `(m, 256)`;
   * and `(c, 1536)`, where:
     - for `bpos > 0`: `c = c0 << (8 − bpos)`, plus `c3/2` when `bpos = 1`; then
       `c = min(bpos, 5)·256 + c1/32 + 8·(c2/32) + (c & 192)`;
     - for `bpos = 0`: `c = c3/128 + (c4 >> 31)·2 + 4·(c2/64) + (c1 & 240)`.
10. Return the mixer output.

## 7. Final refinement

Seven APMs refine the orchestrator's output `q`: `A` with 256 contexts, and `A1`–`A6` with
65536 each. Each bit:

1. Compute the two order hashes:
   * `h2 = hash(B(1), B(2)) & 0xFFFF`;
   * `h3 = hash(B(1), B(2), B(3)) & 0xFFFF`.
2. Compute the APM outputs:
   * `p = A(q, c0)`;
   * `p1 = A1(q, c0 + 256·B(1))`;
   * `p2 = A2(q, c0 ^ h2)`;
   * `p3 = A3(q, c0 ^ h3)`.
3. Average the first set: `q' = (q + p1 + p2 + p3 + 2) >> 2`.
4. Refine `p` with the second set, then average:
   * `p1 = A4(p, c0 + 256·B(1))`, `p2 = A5(p, c0 ^ h2)`, `p3 = A6(p, c0 ^ h3)`;
   * `p = (p + p1 + p2 + p3 + 2) >> 2`.
5. Combine: the final prediction is `(p + q' + 1) >> 1`.

The prediction before the first bit is 2048. The models first run after the first bit
is coded.

## 8. Notes for implementers

* The order of every update, context set, random draw and mixer input is part of the format.
* The JPEG and image models may run inside any block, not only typed ones. The fax model
  always runs from level 4 up.
* Memory grows with the level: about 35 MB at level 1, 233 MB at level 5 and 1.6 GB at
  level 8.
