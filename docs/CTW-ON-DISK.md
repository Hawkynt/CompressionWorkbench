# 🌳 CTW 0.1 on disk

The `.ctw` files of the CTW (Context Tree Weighting) compression utility version 0.1 by Erik
Franken and Marcel Peeters (Eindhoven University of Technology, 2002), the implementation of the
method of Willems, Shtarkov and Tjalkens that compression benchmarks list as "CTW".

## 📜 Provenance

| | |
|---|---|
| Reference | `ctw-v01_source.tar.gz` and `ctw-v01_win32.zip` from `www.ele.tue.nl/ctw/download/`, retrieved from the Internet Archive (captures of 2006-09-06/07). The Win32 `ctw.exe` has SHA-256 `8acba769575418f363f4c92270ee87fa99c9ddf7494efd59e92f04c4437ea39d`. |
| Licence | none: the sources carry only a disclaimer and the notice that the material is for research and development, and that KPN held patents on the method (EP0913035B1, EP0855803, US6150966, US5986591, US5864308, all filed 1997–1998 and expired). |
| Method | Sourcing rung 2. The sources were read to write this specification; `FileFormat.Ctw` is implemented from this document, not from the sources. The numeric tables are copied exactly into `CtwTables.cs`: they are constants of the format. |
| Oracle | the Win32 binary above, and the same sources built with MinGW-w64 GCC, which writes identical files. |

The method papers are Willems, Shtarkov, Tjalkens, *The Context-Tree Weighting Method: Basic
Properties*, IEEE Trans. IT 41(3), 1995, and Willems, Tjalkens, *Complexity Reduction of the
Context-Tree Weighting Algorithm: A Study for KPN Research*, EIDMA report RS.97.01 ("[EIDMA]"
below), which the implementation follows for its integer arithmetic.

## 🧾 Header (12 bytes, multi-byte fields big-endian)

| Offset | Size | Field |
|---:|---:|---|
| 0 | 2 | header size, 12 (a reader skips anything beyond 12) |
| 2 | 1 | file version, 0 |
| 3 | 1 | flags, 0 (ignored) |
| 4 | 4 | uncompressed size *n* |
| 8 | 1 | tree depth *D* (1–12) |
| 9 | 1 | probes *T*: how many slots a node search tries (1–32) |
| 10 | 1 | bits 0–4: log2 of the node-table size *N*; bit 5: strict pruning; bit 6: root weighting; bit 7: zero-redundancy estimator (clear: Krichevsky–Trofimov) |
| 11 | 1 | bits 0–3: log2 of the file-buffer size *F* minus 9; bits 4–7: log2 of the bound *B* on the stored log-beta |

The tool's defaults are *D* = 6, *T* = 32, *N* = 2^22, *F* = 2^22, strict pruning on, root
weighting off, zero-redundancy on, *B* = 1024. The buffer actually used is *F'* = min(*F*, *n*).

## 🔢 Bits and contexts

Each byte is coded as 8 binary decisions, most significant bit first. Bit *k* (0–7) of byte
*x* has its own context tree (*phase* *k*). Its context string is:

- position 0: the bits of *x* already coded, in place, the rest zero (`x & mask[k]`, mask =
  00, 80, C0, E0, F0, F8, FC, FE);
- positions 1, 2, …: the previous bytes, most recent first.

A path through tree *k* uses position *d* − 1 of the string at depth *d*; depths run from 0 (the
root) to *D* + 1, so *D* previous bytes take part.

The first *D* + 1 bytes (or the whole input, if shorter) are coded with probability ½ per bit
and build no model. The context string for byte *D* + 1 then holds, at position *p* ≥ 1, the
byte at file offset *D* + 2 − *p*: the reference fills it one place out of step, so position 1
is the byte at offset *D* + 1 — not yet seen, read as **zero** — and the oldest byte is dropped.
After each byte the string shifts by one and the byte enters at position 1, so the offset
disappears after *D* + 1 bytes. A reader has to reproduce this exactly.

The byte at offset *D* + 1 is read from the file buffer before it is written, so the reference
reads uninitialised memory there. **Zero is what this implementation uses**; the reference's own
memory held zero in every default-depth (6) case measured, so those files agree byte for byte in
both directions. At some other depths and sizes the Win32 binary found a non-zero byte there
(measured with a build that injects the byte: e.g. about 128 at depth 3 for a 20 000-byte input),
and a MinGW build of the same sources even fails to decode its own output at depths 3, 8 and 12.
Such files depend on the heap of the machine that wrote them; the format carries no checksum, so
a reader cannot tell. The fixtures are restricted to cases where the reference and a build that
zero-fills the buffer write identical files.

At depth 12 the reference's context string (13 entries) is written two entries past its end
after every byte; no zero-byte assumption reproduces its files, so depth 12 is refused.

## 🌲 The node table

All 8 trees share one table of *N* nodes. A node holds a 32-bit tag and a record of three
values: the counts *a* (zeros) and *b* (ones), each 0–255, and log-beta, a signed 16-bit value.
A tag is `((probes − 1) << 3 | phase) << 24 | position`, where *position* is an offset in the
file buffer at which this node's context occurs, and *probes* is the probe count at which the
node was placed. An empty slot has the tag FFFFFFFF; the table starts empty.

**Roots.** Root of tree *k* sits at slot `Offset[k] & (N − 1)` (`Offset` = `CtwTables.NodeOffsets`),
with tag `(0 << 3 | k) << 24 | (D + 2)` and record (*a*, *b*) = (1, 0) or (0, 1) after bit *k*
of the last of the first *D* + 1 bytes, log-beta 0.

**Child search** from the node in slot *s* at depth *d* − 1, for context symbol *c* (position
*d* − 1 of the string): step = `(Offset[c] & (N − 1)) ^ ((k + 1) << 1)`; for probe *t* = 1…*T*,
slot `s ← (s + step) & (N − 1)`:

- if the slot is empty: the child is new at probe *t*, unless the table is frozen (≥ *F'* bytes
  coded so far), which makes the search fail;
- else if the tag's top byte equals `(t − 1) << 3 | k` and the symbol stored for it equals *c*:
  found. The stored symbol is the file-buffer byte at the tag's position, masked with
  `mask[k]` when *d* = 1;
- after *T* probes the search fails.

**Path search** for the byte at file offset *i* (the count of bytes coded so far) walks from the
root, with a file-buffer cursor starting at *i* and decremented on every step down:

The *path length* is the number of nodes recorded (the deepest has index length − 1).

1. At each node record it on the path. If it is at depth *D* + 1, stop (path length *D* + 2).
2. Step to depth *d* (cursor − 1) and search the child. Failure: stop, path length *d*.
   Found: continue with it.
3. New (an empty slot, probe *t*) — the context was pruned: the current node stands for a
   single earlier occurrence at buffer offset *q* = (its tag position) − 1. Loop:
   1. With strict pruning, and if *q* differs from cursor + 1, compare the whole remaining
      context, depths *d* … *D* + 1: buffer byte *q* + *d* − *e* (masked at *e* = 1) against
      string position *e* − 1. If all match, stop with path length *d* (no node is created).
   2. Create the node: tag position cursor + 1, probe *t*, record all zero; put it on the path.
   3. Compare the old symbol (buffer byte *q*, masked at *d* = 1) with the new one. If they
      are equal: the new node takes the tag position *q* and the parent's record (also on the
      path), becomes the current node; if it is at depth *D* + 1 stop (length *D* + 2), else
      step to depth *d* + 1 (cursor − 1) and search its child. If the search fails: take the old
      symbol as the unmasked buffer byte at (current node's tag position − 1); if it differs from
      the new one, try to create a leaf for it (below); stop with length *d* + 1. If the child is
      found (impossible for a fresh node; the reference reports an internal error) stop with
      length *d* + 1. If it is new, repeat this loop at depth *d* + 1.
   4. If they differ: create a leaf for the old context — a child search for the old symbol
      from the parent; if new, the node gets tag position *q* and the parent's record — and stop,
      path length = depth + 1. The old leaf is not on the path.

## ⚖️ Weighting (log domain, scaled by 128)

The tables `Log2`, `ZeroRedundancyMax`, `ZeroRedundancyMin` and `Jacobian` (`CtwTables`) give
scaled logarithms; J(*x*) = `Jacobian[−x]` for −1151 ≤ *x* ≤ 0, 0 below.

From the deepest node on the path (the last one) upwards:

- **Leaf** (deepest node, counts *a*, *b*): γ = `Log2[2a+1] − Log2[2b+1]` (KT); with the
  zero-redundancy estimator and *b* = 0: γ = `ZRMax[a] − ZRMin[a]`; with *a* = 0:
  γ = `ZRMin[b] − ZRMax[b]`.
- **Inner node** (each node above it, down to depth 1, or depth 0 with root weighting), with
  the γ from below: if γ ≥ 0, w0 = −J(−γ), w1 = w0 − γ; else w1 = −J(γ), w0 = w1 + γ. The
  node's estimates: with zero redundancy and *b* = 0: e0 = `ZRMax[a]`, e1 = `ZRMin[a]`; *a* = 0:
  e0 = `ZRMin[b]`, e1 = `ZRMax[b]`; otherwise *s* = `Log2[a+b+1]` + 128, e0 = `Log2[2a+1]` − *s*,
  e1 = `Log2[2b+1]` − *s*. With β = the node's log-beta: *u*0 = β + e0, *u*1 = β + e1;
  num = *u*0 + J(−(u0 − w0)) if *u*0 ≥ w0 else w0 + J(u0 − w0); den likewise from *u*1, w1;
  new γ = num − den.
- **Prediction**: from the final γ, if γ ≥ 0 the likely bit is 0 and log p = −J(−γ), else the
  likely bit is 1 and log p = −J(γ). The coder step is −32 · log p, at least 3.

**Updates** are computed for both possible bits before coding and the one matching the coded
bit is written back to every node on the path (index 0 = the root included):

- counts: the coded bit's count + 1; if it was 255 it becomes 128 and the other becomes
  (other + 1) / 2 (integer division);
- log-beta at an inner node: *u*0 − w0 (bit 0) or *u*1 − w1 (bit 1), clamped to ±*B*; at the
  deepest node it is unchanged.

The root (index 0) is computed only when it is the deepest node or with root weighting. In
every other case the record written back to the root is **the one held over from the last time
index 0 was computed**, in either of the two candidate updates, shared by all eight trees and
all zero at the start. The reference keeps those candidates in arrays that live for the whole
run; a reader must keep them too.

After all 8 bits the byte is stored in the file buffer at its offset if that is below *F'*.

## ➗ Arithmetic coder ([EIDMA] ch. 6)

A log-domain binary coder with `Exp`/`Log` tables of 4096 entries, a 13-bit accumulator *A*,
a 24-bit delay register *R* (carry absorber) and a pointer *P* (0 initially, like *A* and *R*).
Each decision codes "is the less likely bit" with step *s* (*s* = 4096 means ½, used for the
first bytes with the actual bit in place of "less likely"):

1. If *P* ≥ 4096: *h* = *P* / 4096; shift *h* bits from the top of *A* into the bottom of *R*
   and *h* bits from the top of *R* out to the stream; *P* −= 4096·*h*.
2. While *R* = FFFFFF: shift one bit the same way.
3. *Q* = *P* + *s*. Less likely bit: if *Q* < 4096, *A* += 2·`Exp[Q]` and *P* = `Log[Exp[P] − Exp[Q]]`;
   else *A* += `Exp[Q − 4096]` and *P* = `Log[2·Exp[P] − Exp[Q − 4096]]` + 4096; an *A* above
   1FFF wraps to 13 bits and adds one to *R*. Likely bit: *P* = *Q*.

The decoder mirrors it with a code accumulator and code delay register filled from the stream
(13 bits first); the less likely bit is decoded when the code registers compare ≥ the threshold
the encoder would have added. At the end the encoder emits *R* (24 bits) and *A* (13 bits). The
first 24 bits shifted out of *R* are never written (they are always zero); bits are packed most
significant first and the last byte is padded with zeros.
