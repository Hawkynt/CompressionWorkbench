# HA archives: on-disk format and the ASC / HSC methods

This is the specification `FileFormat.Ha` and `Compression.Core/Dictionary/Ha` are written from.
HA (Harri Hirvola, 1993–1995) has no published format document beyond the one-page `format` note in
the 0.999 beta distribution, so this was written down from that release (GPL-2.0; read, not copied;
see *Provenance*). It is checked against the reference program in both directions. Every constant
below comes from that release, and none of them is tuned.

All multi-byte integers are little-endian.

## 1. Archive layout

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 2 | `"HA"` |
| 2 | 2 | number of live members |
| 4 | … | member records, back to back, to the end of the file |

The count excludes deleted records (§1.2). The reference walks records until the end of the file
and checks that it found exactly that many.

### 1.1 Member record

| Size | Field |
| --- | --- |
| 1 | `version << 4 \| type` |
| 4 | compressed length `clen` |
| 4 | original length `olen` |
| 4 | CRC-32 of the original data (IEEE 802.3, reflected 0xEDB88320, init and final XOR 0xFFFFFFFF); 0 for directories |
| 4 | modification time, Unix seconds |
| n+1 | path, NUL-terminated |
| m+1 | name, NUL-terminated |
| 1 | machine-information length `k` |
| k | machine information |
| clen | data |

- **Version.** 0.999 writes and accepts only version 2 (its "too old" and "too new" checks are both 2).
- **Type.** 0 CPY (stored), 1 ASC, 2 HSC, 14 DIR, 15 SPECIAL (Unix device, FIFO or symlink data).
  Any other type is an error.
- **Path.** Directory components are separated by byte 0xFF, and a non-empty path ends with one,
  e.g. `in\xFF` or `in\xFFsub\xFF`. A member in the archive root has an empty path. A directory
  record (type 14) has the directory's own name in *name* and its parent in *path*, with `clen` =
  `olen` = 0 and CRC 0.
- **Record size.** The fixed part plus strings is `20 + n + m + k` bytes.
- **Machine information**, first byte:
  - 1, MS-DOS: one DOS attribute byte follows (`k` = 2).
  - 2, Unix: u16 mode, u16 uid, u16 gid follow (`k` = 7).
  - Any other value is ignored.

### 1.2 Deleted records

`ha d` overwrites the first byte of a record with 0xFF and decrements the count. A reader must skip
such a record whole: it still has the full layout, sizes included. The next write compacts the
archive.

### 1.3 Data streams

ASC and HSC streams terminate themselves. The decoder neither needs nor uses `clen`, and the
reference reads past it into the next record. After the last data byte a decoder must see 1-bits
(§2.2), or the real following bytes; the stream's termination makes either work. CPY is `olen`
raw bytes. `olen` = 0 means no stream at all (`clen` = 0). The reference tries the selected
methods in order and keeps the shortest, with CPY always the last resort.

## 2. Binary arithmetic coder (shared by ASC and HSC)

Integers are 16-bit unsigned (`low`, `high`, `value`), with a pending-underflow counter. All
truncation is modulo 2^16.

### 2.1 Interval update

For a symbol occupying `[lo, hi)` of a total `tot`:

    r    = high - low + 1                 (32-bit; 0x10000 when the interval is full)
    high = low + (r*hi / tot) - 1
    low  = low + (r*lo / tot)

Then renormalise. On the decoder side each shift also pulls a fresh bit into `value`:

1. While the top bits (0x8000) of `low` and `high` are equal: shift both left one bit, set bit 0
   of `high`.
2. While `low` has 0x4000 set and `high` does not: `low = (low << 1) & 0x7FFF`,
   `high = (high << 1) | 0x8001`. The decoder additionally flips 0x8000 of `value` after its shift
   and before the new bit is ORed in.

**Encoder output, step 1.**
- The first equal-top-bit shift emits the top bit of `low`, then the pending count's worth of the
  opposite bit, and clears the count.
- Each further step-1 shift emits the top bit of `low`.
- Each step-2 shift increments the pending count instead of emitting.

**Decoder threshold.** Asking "which symbol" for a total `tot` uses:

    ((value - low + 1) * tot - 1) / r     (32-bit, r as above)

The decoder then finds the symbol whose `[lo, hi)` contains that value and applies the same update.

### 2.2 Bits and bytes

- **Packing.** Bits are packed most-significant first.
- **Decoder start.** `low` = 0, `high` = 0xFFFF, `value` = the first two stream bytes as a
  big-endian word. Bit input continues with the third byte. A read past the end of the data yields
  1-bits.
- **Encoder start.** `low` = 0, `high` = 0xFFFF, pending count 0.
- **Encoder end.**
  1. Increment the pending count.
  2. Emit the 0x4000 bit of `low`.
  3. Emit the pending count's worth of its complement.
  4. If a partial byte remains, pad it with 0-bits.

## 3. ASC: LZ77 with adaptive arithmetic-coded tokens

### 3.1 Constants

| Constant | Value | Meaning |
| --- | --- | --- |
| window | 31200 | dictionary size; also the cap of the "bytes seen" counter `seen` |
| min length | 3 | |
| length slots | 64 | short slots 0–14 = lengths 3–17, slot 15 = length 786, slots 16–63 carry 4 extra bits |
| char escape-neighbour radius | 8 | |
| length escape-neighbour radius | 4 | |
| char table | 256 leaves, step 1, rescale at 1000, cutoff 3 | |
| length table | 64 leaves, step 8, rescale at 6000, cutoff 24 | |
| distance-class table | 16 leaves, step 24, rescale at 6000 | |
| type table | 4 contexts × {char, pair}, step 40, rescale at 6000 | |

### 3.2 Frequency trees

- **Layout.** A table of N leaves (N a power of two) is an array of 2N words. Leaf `s` is at
  `N + s`, node `i` = node `2i` + node `2i + 1`, the root is node 1, and node 0 is always 0.
- **add(s, step).** Add `step` to leaf `s` and every ancestor. Afterwards, if the root ≥ the
  table's rescale limit, *rescale*.
- **rescale.** Halve (floor) every leaf greater than 1, then rebuild the inner nodes.
- **zero(s).** Subtract leaf `s`'s value from it and from its ancestors.
- **Cumulative frequency of `s`.** Walk from leaf `N + s` up to the root. Whenever the current
  index is odd, add the node just left of it. This includes node 0 at the root, which is 0.
- **Decoder search for a target `t`.** Start at node 2 with an accumulator of 0. Repeat:
  1. If accumulator + node ≤ `t`, add the node to the accumulator and step to the next sibling.
  2. If the index is now ≥ N, the leaf is `index - N`; stop.
  3. Otherwise descend to the left child (`index * 2`).

### 3.3 Model state and initialisation

- **Character tree** C, all 0. **Character-escape tree** EC, all leaves 1.
- **Length tree** L, all 0. **Length-escape tree** EL, all leaves 1.
- **Distance-class tree** P, all 0, then `add(P, 0, 24)`.
- **Escape weights:** char `ce` = 1, length `le` = 8.
- **Type contexts:** `ctx` = 0; for every context, char weight = pair weight = 40.
- **Counters:** `seen` = 0, `classes` = 1, `pmax` = 1.

### 3.4 Token sequence

Each token starts with a three-way type symbol in context `ctx`. With `a` = char weight,
`b` = pair weight and `w = a + b`, the total is `w + 1`:

| Symbol | Interval | Then |
| --- | --- | --- |
| char | `[0, a)` | `a += 40`; `ctx = (ctx << 1) & 3` |
| pair | `[a, w)` | `b += 40`; `ctx = ((ctx << 1) \| 1) & 3` |
| end | `[w, w + 1)` | stop |

If `w` (taken before the increment) ≥ 6000, halve both weights of that context, with a floor of 1.

**Literal byte `c`.**

1. If C's leaf `c` is 0, the byte is new:
   1. Code escape `[C.root, C.root + ce)` of `C.root + ce`.
   2. Code `c` in EC (cumulative / leaf / EC.root).
   3. `zero(EC, c)`.
   4. `ce` = `ce + 1` if EC.root ≠ 0, else 0.
   5. For every `k` from `max(0, c - 8)` up to but excluding `min(255, c + 8)` whose EC leaf is
      non-zero: `add(EC, k, 1)` with EC's rescale limit of 1000.
2. Otherwise code `c` in C with total `C.root + ce`.
3. In both cases, then:
   1. `add(C, c, 1)`.
   2. If C's leaf `c` is now exactly 3: `ce` -= 1 if `ce` > 1, else `ce` = 1.
   3. Emit `c`. If `seen` < 31200, `seen` += 1.

**Match (`length`, `distance`).** `distance` 0 is the previous byte.

1. **Enable distance classes.** While `seen` > `pmax`: `add(P, classes, 24)`, `classes` += 1,
   `pmax` *= 2.
2. **Distance class.** The class is `distance`'s bit length (0 → 0, 1 → 1, 2–3 → 2, …). Code it
   in P (cumulative / leaf / P.root, all read before updating), then `add(P, class, 24)`.
3. **Distance bits.** If the class `j` > 1, let `top = 1 << (j - 1)`. Code `distance - top`
   uniformly over a range of `seen - pmax/2` when `top` = `pmax/2`, else over `top`. A uniform
   code of `v` over `n` is the interval `[v, v + 1)` of `n`.
4. **Length slot.** Let `x = length - 3`.
   - `x` = 783: slot 15.
   - `x` < 15: slot `x`.
   - Otherwise: slot `((x - 15) >> 4) + 16`, plus 4 extra bits `(x - 15) & 15`.

   Code the slot exactly like a literal, using L / EL / `le`, with these differences:
   - The escape step for `le` is 8.
   - The neighbour radius is 4, over the range `max(0, s - 4)` to below `min(63, s + 4)`.
   - EL rescales at 6000.
   - The C-equivalent update is `add(L, s, 8)`.
   - The cutoff test is leaf = 24: `le` -= 8 if `le` > 8, else `le` = 1.
5. **Extra bits.** If the slot has them, code them uniformly over 16.
6. **Copy.** If `seen` < 31200: `seen` = `min(31200, seen + length)`. Copy `length` bytes, each
   from `distance + 1` positions back in the 31200-byte circular window; overlapping copies
   repeat.

Every distance the format can express is < `seen`. A decoder may refuse anything else, because the
reference never writes it.

**End.** The stream ends with the *end* type symbol, then the coder termination (§2.2).

## 4. HSC: order-4 finite-context model

A PPM-style model over byte contexts of length 0–4, with exclusion, a fixed pool of contexts and
frequency nodes recycled by least-recent use, and an order −1 fallback coding uniformly over the
unexcluded byte values plus end-of-stream (symbol 256).

### 4.1 Constants

| Constant | Value |
| --- | --- |
| contexts | 10000 |
| frequency nodes | 32760; nodes 0–9999 are the contexts' first nodes, 10000–32759 the free pool |
| hash buckets | 16384 |
| maximum context length | 4; drops permanently to 3 at the 2500th recycling of a length-4 context |
| low-frequency threshold | 3 (a node with frequency < 3 counts as "low") |
| context total limit | 8000 (halve when reached) |
| refresh counter start | 4 |
| initial-escape counters | 5 (one per length), limit 32; length 0 starts at 16, lengths 1–4 at 15 |
| no-escape counter | `nec`, saturating at 10; it scales the first context's frequencies at ≥ 5 |
| node "none" | 0xFFFF |

### 4.2 State

**Per context `x`:**
- length (0xFF = unused)
- the four context bytes it was created with
- the next context in its hash bucket
- its position in an LRU order over all 10000 contexts
- `total`, the sum of its node frequencies
- `low`, its count of low nodes
- `extra`, the number of nodes after the first
- `refresh`

**Per node:**
- frequency
- symbol byte
- next node, forming a singly-linked chain that starts at the context's own node

**Global:**
- current context bytes `cur[0..3]`, where `cur[0]` is the most recent byte; all 0 at the start
- an exclusion set of byte values, with its size
- an update stack of up to 5 entries (context, node, escaped flag)
- `nec`
- the free-node list
- the release cursor, starting at context 0
- the LRU order

**Hash.** Hash `H(L)` of the current bytes, with a table `R[16384]`:

    H(0) = 0
    H(1) = R[cur[0]]
    H(L) = R[(cur[L-1] + H(L-1)) & 16383]

A context created with length `L` sits in bucket `H(L)` of its own bytes, inserted at the bucket's
head.

`R` is the Park–Miller minimal standard generator (multiplier 16807, modulus 2^31 − 1) seeded with
10. `R[i]` is the low 14 bits of the generator's (i+1)-th output.

### 4.3 Initial state

- Every context is unused, with no next node.
- The LRU order is 0 (most recent) … 9999 (least recent).
- The free list is 10000, 10001, …, 32759.
- `nec` = 0, and the exclusion set is empty.

### 4.4 Coding one byte `c`

Encoder and decoder run the same steps; the decoder recovers `c` where the encoder codes it.

1. **Find the longest context.**
   1. Clear the exclusion set and the update stack.
   2. Search lengths 4 down to 0, even when the maximum length has dropped to 3.
   3. A context matches when it has exactly that length and its first `L` stored bytes equal
      `cur[0..L-1]`. Take the first match in bucket order.
   4. Later searches ("next") continue strictly below the length found last.
2. **Plan new contexts.** Let `lo` = found length + 1 (0 if none was found) and `hi` = current
   maximum length + 1.
3. **Code the symbol.** Starting with the found context, repeat:
   - If there is no context left, code at order −1 (§4.7) and stop.
   - Otherwise code in the context (§4.5 while the exclusion set is empty, §4.6 after).
     - If the symbol was there, move that context to the LRU front and stop.
     - On an escape, take the next context down.
4. **Update** (§4.8).
5. **Create the missing contexts.** For `L` = `hi - 1` down to `lo`, create a context of length
   `L` (§4.9) whose first node holds `c`.
6. **Shift the history.** `cur` = `[c, cur[0], cur[1], cur[2]]`.

**End of stream.** After the last byte the encoder codes symbol 256:
- an escape in every context from the longest one down
- then 256 at order −1
- then the coder termination

The decoder stops when order −1 yields 256, and makes no update for it.

**Escape weight `adj(e, x)`.** For a raw escape count `e` in context `x`:

- If `total(x)` = 1: 2 if the initial-escape counter for `x`'s length is ≥ 16, else 1.
- Otherwise, if `extra(x)` = 255: 1.
- Otherwise, if `extra(x)` > 0 and `2·(extra(x)+1)` ≥ `total(x)`:
  - `e = e·2·(extra(x)+1) / total(x)`, in integer arithmetic.
  - If additionally `extra(x)+1` = `total(x)`: `e += (extra(x)+1) >> 1`.
- Return `e`, or 1 if `e` = 0.

### 4.5 First context (nothing excluded)

1. Walk the chain in order. `sum` is the frequency before `c`'s node, `f` its frequency (0 if
   absent). Let `t` = `total(x)` and `esc` = `adj(low(x), x)`.
2. **Scaling.** If `nec` ≥ 5, scale `t`, `sum` and `f` up by 4 when `t` ≤ 4 and `nec` = 10, else
   by 2. The decoder looks up with the threshold of `t·scale + esc` divided back down by the
   scale, using the unscaled chain.
3. **Found.**
   1. Code `[sum, sum+f)` of `t + esc`.
   2. If `nec` < 10, `nec` += 1.
   3. If `total(x)` = 1 and its length's initial-escape counter > 0, decrement the counter.
   4. Push (x, that node, not escaped).
4. **Escape.**
   1. Code `[t, t+esc)` of `t + esc`.
   2. Add every chain symbol to the exclusion set.
   3. `nec` = 0.
   4. If `total(x)` = 1 and the counter < 32, increment it.
   5. Push (x, the chain's last node, escaped).

### 4.6 Further contexts (some bytes excluded)

1. Consider only the chain nodes whose symbol is not excluded, in chain order:
   - `t` is the sum of their frequencies.
   - `e` is how many of them are low.
   - `sum` and `f` are as before, over these nodes.
   - `esc` = `adj(e, x)`.
   - No scaling.
2. **Found.**
   1. Code `[sum, sum+f)` of `t + esc`.
   2. `nec` += 1.
   3. Adjust the initial-escape counter as in §4.5.
   4. Push (x, node, not escaped).
3. **Escape.**
   1. Code `[t, t+esc)` of `t + esc`.
   2. Exclude those symbols.
   3. Adjust the counter as in §4.5.
   4. Push (x, the chain's last node, counting all nodes, escaped).

### 4.7 Order −1

- With `k` excluded values, the total is `257 - k`.
- Symbol `s` (0–256) has the interval `[u, u+1)`, where `u` is the number of unexcluded byte
  values below `s`.

### 4.8 Update

Pop the stack from the top, the most recently pushed entry first. For each (x, node, escaped):

1. **Count the symbol.**
   - **Escaped.**
     1. If the free list is empty, *release* (§4.10).
     2. Take the free list's first node and append it after `node`, with frequency 1 and symbol `c`.
     3. `extra(x)` += 1 and `low(x)` += 1.
   - **Not escaped.** The node's frequency += 1. If it became 3, `low(x)` -= 1.
2. **Track the refresh counter.** `total(x)` += 1. Then:
   - If 2·(the frequency just updated) < `total(x) / (extra(x)+1)`, in integer division:
     `refresh(x)` -= 1.
   - Otherwise, if `refresh(x)` < 4: `refresh(x)` += 1.
3. **Halve if due.** If `refresh(x)` = 0 or `total(x)` ≥ 8000:
   1. `refresh(x)` += 1.
   2. Recompute `total` and `low` from zero. Every node with frequency > 1 is halved (floor) and
      counted; a node with frequency 1 stays 1 and counts as 1 and as low.

### 4.9 Creating a context

1. Take the least-recently-used context and make it the most recent.
2. **If it was in use:**
   - If it had length 4, count one length-4 recycling (§4.1).
   - Remove it from its hash bucket.
   - Return its chain's nodes after the first to the free list.
3. **Fill it.**
   - Its first node gets frequency 1 and symbol `c`, with no next node.
   - `total` = `low` = 1, `extra` = 0, `refresh` = 4.
   - Set its length, and copy `cur[0..3]` as its bytes.
4. Insert it at the head of bucket `H(length)`.

### 4.10 Releasing nodes

1. **Pick a victim.** Advance the release cursor cyclically to the next context whose chain has at
   least two nodes, skipping any context still on the update stack.
2. Let `d` = 1 + the smallest frequency in its chain.
3. **If the first node's frequency < `d`:**
   1. Let `i` be the first later node with frequency ≥ `d`, or the chain's last node.
   2. Copy `i`'s frequency and symbol into the first node.
   3. Free every node from the second through `i`.
   4. If nothing follows `i`: `extra` = 0, `total` = the first node's frequency, `low` = 1 if that
      is < 3 (else 0). Stop.
4. **Otherwise**, or after step 3 when more nodes follow:
   1. Divide the first node's frequency by `d`. `total` = it, `low` = 1 if it is < 3, `extra` = 0.
   2. Free every later node with frequency < `d`.
   3. Divide each remaining node's frequency by `d`, add it to `total`, increment `extra`, and
      increment `low` when the result is < 3.

## Provenance

- **Source.** HA 0.999 beta, as published at <https://github.com/val-khokhlov/ha>, revision
  f112fbeb282c2393639393f759a11887c0dad3b5 (GPL-2.0-or-later). It was read to write this
  document; no code was taken.
- **Oracle.** That repository's release `v0.999b` `ha.exe`, a Win32 build of the same source.
- **Original 1995 archive.** `HA_SRC.HA` from the original 0.999 beta distribution
  (archive.org item `ha-0.999beta`, md5 32bc5e964219c3fc00bef392efc2fb77). It was written by the
  original DOS `ha.exe`, which the author built partly from hand-optimised assembly. Decoding it
  is the check that this description matches the original, not only the C port.
- **Verification.** Covered by the tests in `Compression.Tests/Ha/` and described in
  `Compression.Tests/Ha/ReferenceVectors/README.md`.
