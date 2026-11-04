# sfArk V2 on disk

sfArk (Andy Inman / melodymachine.com, 1998-2003) packs SoundFont 2 banks: the
16-bit sample data is coded losslessly with prediction plus Rice-style codes,
everything else is zlib. This page is the specification CompressionWorkbench
implements from. It was written by reading the decoder in sfArkLib (GPL-3.0,
github.com/raboof/sfArkLib, revision `c710d851`) and running its command-line
front end `sfarkxtc` as an oracle (sourcing rung 2). No code was carried across.
sfArk V1 (compression methods 0-2) uses an undocumented metadata coder and is out
of scope; the reference decoder refuses it too.

All integers are little-endian. "Adler" below is zlib's Adler-32 update function,
but **seeded with 0**, not with 1.

## Header

The header is normally at offset 0; a self-extractor stub may precede it. A reader
checks offset 0 first, then every offset from 131071 down to 1, and takes the first
place whose signature is `sfArk`, whose method is >= 3 and whose header check is
right.

| Offset | Size | Field |
|-------:|-----:|-------|
| 0  | 4 | flags: bit 0 = notes text present, bit 1 = licence text present |
| 4  | 4 | original (SF2) size in bytes |
| 8  | 4 | sfArk file size, header included |
| 12 | 4 | file check (see below) |
| 16 | 4 | header check: Adler(0, header bytes with this field zeroed, header length) |
| 20 | 1 | version needed, tenths (20 = 2.0); the reference refuses values above 30 |
| 21 | 5 | creator version, ASCII, e.g. `2.15 ` |
| 26 | 5 | `sfArk` |
| 31 | 1 | method: 4 Turbo, 5 Fast, 6 Standard, 7 Max (0-2 are V1, 3 and 8 are never decodable) |
| 32 | 2 | file type, 0 = SF2 |
| 34 | 4 | audio start: offset of the sample data in the original |
| 38 | 4 | post-audio start: offset just past the sample data |
| 42 | n+1 | original file name, NUL-terminated (at most 255 characters) |

Header length = 42 + name length + 1. Every published file seen so far was written
by version 2.15 with version-needed 20.

## Optional text files

Directly after the header, byte-aligned: when flag bit 1 is set a licence text, then
when bit 0 is set a notes text. Each is a u32 length (1..262144) followed by that
many bytes of a zlib stream; its inflated bytes feed the file check with Adler.

## The bit stream

Everything after that is one bit stream: 16-bit little-endian words, each read from
its most significant bit down. Primitives:

- **bits(n)**: the next n bits (n <= 16), first bit most significant.
- **unary**: count 0 bits up to and including the next 1 bit; the count is the value.
- **delta(p)**: m = unary; when m != 0 one more bit, 1 meaning negative. Value p + ±m,
  kept to 16 bits (two's complement).
- **byte**: bits(8). Byte runs inside the bit stream are therefore *not* aligned.

## Sections

The original is rebuilt front to back. It starts in the pre-audio section.

- **Pre-audio / post-audio block**: 4 bytes (u32 length n <= 262144), then n bytes of
  a zlib stream inflating to at most 262144 bytes. Its bytes are output and fed to the
  file check with Adler. Afterwards: when the output has reached the original size the
  file is done; when in pre-audio and the output has reached audio start, the audio
  section begins.
- **Audio block**: normally the method's block size in samples (Fast: 1024; Turbo,
  Standard, Max: 4096). When that would reach or pass post-audio start, the block is
  shortened to end exactly there and the next section is post-audio.

So a file without audio may simply set both audio offsets to the original size.

## Audio blocks

Decoder state that lives for the whole file, all starting at zero unless stated:
`fix` (starts at 8), `loops`, `loops4`, `shift`, `lastShift`, twenty 16-bit history
values `h[0..19]`, the LPC state below, and the file check.

### Residual windows

A block of N samples is cut into windows of W samples (Turbo: 256; others: 32), the
remainder forming a last, shorter window. Per window: `fix = delta(fix)`, then

- `fix` 0..13: per sample, `lo = bits(fix+1)`, `g = unary`,
  `value = ((g << fix) | (lo >> 1)) XOR (lo & 1 ? 0xFFFF : 0)`;
- `fix` 14: per sample `bits(16)`;
- `fix` -1: per sample one bit, 1 meaning -1, 0 meaning 0;
- `fix` -2: all samples 0, no bits;
- anything else: corrupt.

### Turbo (method 4)

`k = delta(loops)`, 0 <= k <= 3, `loops = k`. Read the residual windows. Then for
j = k-1 down to 0: integrate (`out[0] = in[0] + h[j]`, `out[i] = in[i] + out[i-1]`,
`h[j] = out[N-1]`). Just *before* the j = 0 integration the file check becomes
`2·check + sum(buffer)` (see below). With k = 0 the check is not touched.

### Fast, Standard, Max (methods 5, 6, 7)

1. **Shift map**. One bit: no shifts. Otherwise S = ceil(N/64) slots, position
   `pos = 0`, slot cursor `c = 0`; while the next bit is 1: `pos += bits(B)` where B is
   the bit length of `S - pos - 1` (0 → 0 bits, 1 → 1, 2..3 → 2, …); the new shift is
   `delta(lastShift)` (and becomes `lastShift`) when `shift` is 0, else `delta(0)`;
   slots `c..pos-1` get `shift`, then `shift` = the new value. Finally slots up to S get
   `shift`. Without the shift map, `shift` keeps its value for later blocks.
2. One bit: integrator family. 1: "average" family, `k = delta(loops4)` with
   0 <= k <= max4 (Fast 20, Standard 3, Max 5), `loops4 = k`. 0: "plain" family,
   `k = delta(loops)` with 0 <= k <= max (Fast 20, Standard 3, Max 3), `loops = k`,
   then k one-bit selectors s[0..k-1].
3. Standard and Max only: one bit; when 1, a 32-bit reset mask = `bits(16) | bits(16) << 16`
   (low half first); else the mask is 0.
4. The residual windows.
5. Standard and Max only: LPC synthesis (below), order 8 (Standard) or 128 (Max).
6. For j = k-1 down to 0:
   - average family: `a = h[j]`; per sample `out = in + a`, `a += in / 2` (division
     truncating toward zero); `h[j] = a`. All 16-bit.
   - plain family, s[j] = 0: integrate as in Turbo.
   - plain family, s[j] = 1: backwards: `out[N-1] = in[N-1]`; for i = N-2 down to 1,
     `out[i] = in[i] + ((in[i-1] + out[i+1]) >> 1)` (arithmetic shift of the 32-bit sum);
     `out[0] = in[0] + (out[1] >> 1)`; `h[j] = out[N-1]`.
7. Shifts: every 64-sample slot with a non-zero shift is shifted left by it.
8. File check: `2·check + sum(buffer)`.

`sum(buffer)` adds `x XOR (x >> 15)` over the 16-bit samples (x for x >= 0, -x-1 for
x < 0); the check is kept modulo 2^32.

### LPC synthesis

The decoder keeps, across blocks: a lattice state `u[0..128]` (32-bit), four
autocorrelation slots `A[0..3][0..128]` (single precision), a slot cursor `t`, and a
128-entry "previous start" buffer `P`. A reset clears all of them and sets t = 0.
Decoding starts with a reset.

A block shorter than 128 samples passes through unchanged. Otherwise it is processed
in 128-sample windows (the last may run past the block end; samples past the end are
don't-care). For window number w (bit w of the reset mask):

1. `R[k] = float(A0[k] + A1[k] + A2[k] + A3[k])` for k = 0..p, summed in double.
2. Mask bit clear: reflection coefficients from R by the Schur recursion below, then
   lattice synthesis of the 128 samples. Mask bit set: a reset, and the samples pass
   through unchanged.
3. Add to `A[t]`, for lags k = p down to 1, the cross-window products of the 2p-sample
   sequence `P[0..p-1] ++ out[0..p-1]` (sum of `b[i]·b[i+k]` for i = p-k .. p-1).
4. `t = (t+1) mod 4`; overwrite `A[t][k]` (k = p..0) with the autocorrelation of the
   window's 128 output samples (lag k: sum of `out[i]·out[i+k]`, i < 128-k).
5. `P[0..p-1] = out[0..p-1]` — the *start* of the window.

The sums are bit-exact only when computed like the reference: samples converted to
float; products and running sums in double; the running sum rounded to float after
every group of 16 terms (groups starting at the first index, any remainder one term
at a time); the step-3 total added to the slot in double and rounded to float.

**Schur** (order p, R[0..p]): if R[0] = 0 all coefficients are 0. Else `e = R[0]`,
`G0[i] = G1[i] = R[i+1]`. For i = 0..p-1: `r = -float(G1[0] / e)` (double division);
`e = float(e + G1[0]·r)`; coefficient `c[i] = trunc(float(r · 16384))` as an integer;
then for m = 0 .. p-i-2: `G1'[m] = float(G1[m+1] + r·G0[m])`,
`G0[m] = float(G0[m] + r·G1[m+1])` (both from the values before this step), in double.

**Lattice synthesis** per sample: `s = residual`; for i = p-1 down to 0:
`s -= q(c[i]·u[i])`, `u[i+1] = u[i] + q(c[i]·s)` where `q(x)` divides by 16384
truncating toward zero (64-bit products, 32-bit results); then `u[0] = s`, output s.
Output samples are 32-bit and are only cut to 16 bits when the LPC stage hands them on.

## File check

The header's file check must equal the accumulated value: Adler (seeded 0) over the
inflated text files, then per block either Adler over the inflated bytes or the audio
update `2·check + sum`, in file order.

## Writer notes

A writer that only uses method 5 (no shifts, plain family, k = 1, s[0] = 0) produces
files the reference accepts; choosing per window the `fix` with the fewest bits is
enough for useful compression. Non-audio blocks must end exactly at audio start.
