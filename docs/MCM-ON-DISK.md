# MCM 0.84 — on-disk notes

`mcm` is Mathieu Chartier's context-mixing archiver (Google, 2013–2016). Its archive
starts with the ten ASCII bytes `MCMARCHIVE`. There is no published format document; the
program is GPL-3.0, which this LGPL-3.0-or-later repository cannot absorb, so this file
is the firewall: the source was read to write down **what the format is**, and our
implementation (`Compression.Core/Entropy/ContextMixing/Mcm`,
`Hawkynt.FileFormats.Archives/FileFormats/FileFormat.Mcm`) was written from this file.
Tables of constants are copied exactly — they *are* the format.

## Provenance

| What | Value |
|---|---|
| Source read | `github.com/mathieuchartier/mcm`, master tarball, `HEAD` = `1c44c95a` at retrieval, files dated 2017-01-08; prints `mcm compressor v0.84` |
| Oracle | the same tree built with MinGW-w64 g++ (`-O3 -std=c++11`, one template keyword added in `GD.hpp` so it compiles); `mcm.exe` |
| Measured with it | every fixture under `Compression.Tests/Mcm/Vectors`, byte comparison of our encoder's archives against its archives, and its decoder run on ours |

Everything marked **measured** was confirmed against that binary; everything else is
read from the source and then exercised by the round trips in the tests.

## Command line, as far as it shapes the bytes

`mcm [-{t|f|m|h|x|s}N] [-store] [-filter=none|dict|x86|auto] [-lzp=auto|true|false] in [out]`
compresses one file or one directory tree; `mcm d archive` extracts into the **current
directory under the stored names** (the output argument is ignored). `N` is the memory
level 0–11 (default 6); the level letter picks the compressor (default `m`). The
non-release build also writes a debug file `of.txt` into the working directory on every
run — harmless, but tests must run it in a scratch directory.

| Option | Compressor type byte | Model inputs | Notes |
|---|---|---|---|
| `-store` | 0 Store | – | filters still run |
| (wav data) | 1 Wav16 | – | chosen by the analyzer, see *Wav16* |
| `-t` | 2 CM turbo | 3 | |
| `-f` | 3 CM fast | 4 | |
| `-m` | 4 CM mid | 6 | default |
| `-h` | 5 CM high | 10 | |
| `-x` | 6 CM max | 13 + SSE | **measured: 0.84 crashes** (null `opts` pointer dereferenced while building the binary profile) and leaves a 0-byte file. No `-x` archive can exist; we refuse type 6. |
| `-s` | 7 CM simple | 6 | undocumented; uses the *simple* profile regardless of block type |

## Container

All multi-byte integers are LEB128 (7 bits per byte, low group first, high bit = more)
unless stated.

    "MCMARCHIVE"                      10 bytes
    major version                      u16 big-endian = 0
    minor version                      u16 big-endian = 84
    metadata length M                  LEB128 — bytes of the *uncompressed* metadata
    metadata                           CM stream (see below), M bytes when decoded
    check                              LEB128 = 1234  (bytes D2 09)
    solid block 0 .. n-1               in metadata order

The reader insists on exactly 0.84.

**Metadata** is compressed with the CM coder, mid level (6 inputs), memory level 6, LZP on,
forced *text* profile — always, whatever the user chose. Decoded, it is

    block count                        LEB128
    per block:
      memory level                     u8
      compressor type                  u8 (table above)
      LZP enabled                      u8 (0/1)
      filter                           u8: 0 none, 1 dict, 2 x86
      profile                          u8: 0 text, 1 binary, 2 wave16
      segment count                    LEB128
      per segment:
        file index                     LEB128 (index into the file list)
        base offset                    LEB128 (always 0 when written)
        range count R                  LEB128 (≥ 1)
        R × range length               LEB128
        R × gap                        LEB128: offset = previous range end + gap
                                       (previous end starts at 0)
    file list:
      count N                          LEB128
      N × name suffix                  bytes up to a 0 terminator
      N × shared prefix length         LEB128: name = previous full name[0..len] + suffix
                                       (ignored for the first)
      N × attributes                   u8: 1 directory, 2 read, 4 write, 8 execute,
                                       16 system, 32 hidden

A **solid block** is the concatenation, in segment order, of every range of every
segment, read from the named files at `base + offset`. On disk it is

    filtered length                    LEB128, then zero bytes up to 10 bytes in all
    payload                            compressor stream that decodes to
                                       `filtered length` bytes, which the filter then
                                       turns back into the block's bytes

The payload has no length of its own: a CM or Wav16 stream ends where its range decoder
stopped reading (see *Range coder*), a Store payload is exactly `filtered length` bytes.
The next block starts right after.

### What the writer puts there (needed for byte-identical output)

- **File names**: the command-line argument with one trailing `/` or `\` removed. A
  directory argument adds itself (attributes 0x05 on Windows) and, recursively, its
  entries as `dir/entry` in the order the OS enumerates them; absolute paths lose their
  directory part. Files get attributes 0x0E on Windows (read|write|execute, minus write
  when read-only); the Linux build writes 0x00 or 0x08.
- **File order**: directories first (ordered by full name), then files ordered by the
  *sort extension*, then by file name (text after the last `/` or `\`), then by full
  name. The extension is the text after the last `.` of the last path component; `h`,
  `hpp`, `inl`, `cpp` sort as `c`; `jpg zip 7z apk mp3 gif png` sort as that extension
  prefixed with the byte 0xFF (so after every other extension; names compare as unsigned bytes).
- **Blocks**: one candidate per profile in profile order (text, binary, wave16, …), each
  given every file's ranges of that profile (one segment per file that has any), empty
  ones dropped, then **stably sorted by total size, largest first**.
- **Block algorithm**: compressor from the level; wave16 blocks always Wav16. Text blocks
  get filter dict and LZP on, binary blocks filter x86 and LZP on, wave16 blocks filter
  none and LZP **uninitialised** (the writer stores whatever the stack held; the
  reader never looks). `-lzp=` and `-filter=` override.

## Analyzer

Each file is cut into typed runs by a detector that looks ahead through a 256 KiB
window (refilled to full whenever a new run is detected). Runs are produced one at a
time; each run's bytes are consumed before the next detection.

A detection scans from the current position:

    binary_len = 0
    repeat while binary_len < window size:
        start a *text attempt* at binary_len with fresh state:
            UTF-8 checker, text_len = space_count = word_len = number_len = 0, score = 0
        while binary_len + text_len < window size:
            p = binary_len + text_len
            if the 4 bytes before p (a running big-endian word over everything this
               detector has ever looked at, never reset) spell "RIFF" and a stereo
               16-bit PCM WAVE header follows (see Wav16): return a binary run of the
               header's length and queue a wave16 run (that run is cut short at EOF)
            c = byte at p; shift c into the running word
            feed c to the UTF-8 checker; stop the attempt on a UTF-8 error or on a
               forbidden byte: 0–8, 11, 12, 14–17, 19–31
            text_len += 1; last = previous byte (from the running word)
            score += isspace(c)
            if last != c:
                if c is a letter, ≥ 0x80, '|', '_' or '-':  word_len += 1;
                                                            score += 10·isspace(last)
                else if word_len != 0:  if 3 ≤ word_len < 32: score += 3·word_len
                                        score += 10·isspace(c); word_len = 0
                number_len = c is a digit ? number_len + 1 : 0
                score += 1 if 1 ≤ number_len ≤ 12
                space_count += isspace(c)
            else if c is neither space nor digit:  score -= 1
        if text_len > 64 and 100·space_count > text_len and score > text_len:
            if binary_len == 0: return a text run of text_len bytes
            else stop scanning (the text starts a later run)
        binary_len += text_len; stop if it reached the window end; binary_len += 1
    return a binary run of binary_len bytes

(`isspace` is the C locale set: 9–13 and 32. The UTF-8 checker accepts 7-bit bytes,
lead bytes 110xxxxx … 1111110x with that many continuation bytes `10xxxxxx`; anything
else, including a stray continuation byte, is an error.)

Consecutive runs of the same profile merge. A text run that follows a zero-length binary
run that follows a text run swallows both into the earlier text run. Every byte of a text
run also feeds the **dictionary builder** (see *Dict filter*); the builder is shared by
all files of the archive.

## Filters

Both filters sit behind a 16 KiB staging buffer, and **their output depends on where that
buffer's boundaries fall**, so an encoder that wants mcm's bytes must reproduce it:

- **Forward (compressing)**: the input buffer holds up to 16 384 not-yet-consumed bytes;
  it is topped up from the block whenever the output buffer has been fully drained.
  One *pass* runs the transform over the whole input buffer with an output budget of
  16 384 bytes; whatever input the pass does not consume stays for the next pass.
- **Reverse (decompressing)**: input accumulates to 16 384 bytes, a pass runs, the
  unconsumed rest stays; at the end of the block passes repeat until nothing is left.

### x86 (E8/E9) filter

Forward pass over input `in[0..n)`, output budget `B` (16 384), state `offset` (starts
17) and `last` (starts 17):

    if n ≤ 6: copy all n bytes, done.
    i = 0, o = 0
    while i < n − 6 and o < B − 6:
        b = in[i]; emit b
        candidate = (b & 0xFE) == 0xE8  or  ((b & 0xF0) == 0x80 and i > 0 and in[i−1] == 0x0F)
        if candidate:
            pos = offset + i                     (pos as an unsigned machine word)
            if in[i+4] is 0x00 or 0xFF:
                d = in[i+1] | in[i+2]<<8 | in[i+3]<<16 | in[i+4]<<24   (signed 32-bit)
                if d > 0  or  (d < 0 and −d < (int32)pos):
                    if pos − last > 98 304 (unsigned): offset = 0
                    a = d + (uint32)pos
                    emit in[i+4], (a>>16)^0xA2, (a>>8)^0xA2, a^0xA2, 0x63
                    i += 4; last = pos; (next i) continue
            if in[i+1] is 0xFF, 0x00 or 0xB2:  emit 0xB2, in[i+1]; i += 1
        i += 1
    offset += i (bytes consumed)

Note that `i > 0` looks only inside the current pass, and that resetting `offset`
mid-pass makes every later position in the pass and the next pass's `offset`
pass-relative. mcm's own reverse re-derives positions from *its* buffer boundaries, which
need not coincide with the encoder's; that it nonetheless restored a 620 000-byte file built to
trigger the reset (200 KB without a branch, then code, twice) was measured, not reasoned. Our
decoder does not rely on it and inverts the encoder: it walks the encoded stream token by token and recomputes where
each forward pass started and ended (pass k starts at the first unconsumed original
byte `I` and encoded position `O`; it has `min(16384, total − I)` input bytes and stops
before a token at original `i`, encoded `o` once `i − I ≥ n − 6` or `o − O ≥ B − 6`).
A token is: candidate byte followed by 0x00/0xFF → transformed (5 bytes follow:
sign, three address bytes, marker); candidate followed by 0xB2 → escaped (the next byte
is literal); anything else → one literal byte. The address restores the low 24 bits of
`d` as `a − pos`; the sign byte is stored. A pass with ≤ 6 input bytes is a raw copy.

### Dict filter

Text blocks only. The analyzer's builder counts words: a *word* is a maximal run of
letters or bytes ≥ 0x80 (at most 32 kept), counted when 3 ≤ length when the run ends,
classified by case: all upper → counted lower-cased as *all-caps*; first upper only →
lower-cased first char as *first-cap*; mixed → not counted; else *normal*. (The counter
starts discarding words below a growing threshold only when its 256 MiB arena fills.)

**Code words** (min occurrences 5, 40 one-byte, 32 two-byte reserved, 128 codes):

1. Take words occurring ≥ 5 times. `Savings(n) = len·count − (n·count + capcount + len + 1)`;
   `SavingsVS(n) = Savings(n) − Savings(n+1)` for n < 3, else `Savings(3)`.
2. Sort descending by `SavingsVS(1)`; `num1 = min(40, words)`, then while
   `num1 + 1 < words` and `(128 − num1 − 1)² ≥ words − num1`: `num1 += 1`.
3. The first `num1` words are the 1-byte words (then sorted byte-lexicographically).
4. Remaining words sorted descending by `SavingsVS(2)`. `num3` = smallest value from 0
   with `num3 + num1 < 128 − 32` such that `(128 − num3 − num1)·128 + num3·128² ≥
   remaining`; `num2 = 128 − num1 − num3`. Take up to `num2·128` words as 2-byte words
   (sorted lexicographically).
5. Remaining sorted descending by `SavingsVS(3)`, words with `Savings(3) ≤ 0` dropped
   from the tail, then up to `num3·128²` as 3-byte words (sorted lexicographically).

The sorts are `std::sort` over reversed ranges — not stable; ties among equal savings
are ordered by the libstdc++ introsort we reproduce in `McmIntroSort`.

**Dictionary header** (emitted as the first bytes of the filtered stream):

    total header length                u32 big-endian, counting these 4 bytes
    escape, cap-first, cap-word        3 bytes: 0x03, 0x04, 0x06
    num1, num2, num3, code count       4 bytes (code count = 128)
    words                              each 0-terminated, 1-byte words, then 2-byte, then 3-byte

Code bytes: 1-byte words use 128 .. 128+num1−1. A 2-byte word is `b1 b2` with b1 from
128+num1 and b2 over all 128 codes (128..255): word index `(b1−128−num1)·128 + (b2−128)`.
A 3-byte word likewise with two trailing code bytes.

**Forward** (after the header has been copied out; output budget per pass `B`):

    while input remains and fewer than B − 5 bytes are out:
        if the last emitted original-side byte is not a word byte (letters, ≥ 0x80):
            if in[i] is a word byte:
                L = length of the word run at i (≤ 256, within this pass's input)
                if the run reaches the pass input's end and is not the whole input: end the pass
                for len = L down to min(max(L, 3), 6):
                    case = all-caps / first-cap / normal of in[i..i+len) (mixed → none)
                    look up the lower-cased form; if it is a code word:
                        emit 0x06 if all-caps, 0x04 if first-cap; emit the code bytes;
                        i += len; last = 'a'; continue the main loop
                if L < remaining budget:
                    if all caps:  emit 0x06, the lower-cased word; i += L; last = 'a'; end the pass
                    if first cap: emit 0x04, the word with first char lowered; …; end the pass
            if in[i] is 0x03, 0x04, 0x06 or ≥ 0x80: emit 0x03
        emit in[i]; last = in[i]; i += 1

(Case tests are ASCII only. "Mixed case" means some but not all upper and not just the
first; a mixed-case run can never match, because the dictionary holds lower-case words. When
the run is neither all-caps nor first-cap and no prefix matched, its first byte goes out as an
ordinary byte — escaped if it is ≥ 0x80 — and the rest of the run follows unescaped, since the
previous byte is then a word byte.)

**Reverse**: read the 4-byte length, collect the header, rebuild the word lists, then

    while input remains (all but the last 4 bytes mid-stream):
        c = next
        if previous output byte is not a word byte:
            fc = c == 0x04; ac = c == 0x06
            if c ≥ 0x80 or fc or ac:
                if fc or ac: c = next
                if c ≥ 0x80: look up the word (1, 2 or 3 code bytes as above), emit it with
                    its first char (fc) or all chars (ac) upper-cased; continue
                else if fc and c is a–z: c = upper(c)
                else if ac: capital mode on
            if c == 0x03: c = next
        if capital mode and c is a–z: c = upper(c) else capital mode off
        emit c

## Store

Copies `filtered length` bytes.

## CM coder

A binary context-mixing coder over **reordered** bytes: every byte is replaced by its
index in a 256-entry permutation before modelling, and decoded symbols are mapped back.
Text and simple use the *text order*, binary the *binary order* (both in
`McmTables`).

### Range coder

7-Zip's carry-propagating coder, 12-bit probabilities. State: `low` 64-bit (0),
`range` 32-bit (0xFFFFFFFF), `cache` (0), `cache size` (1).

- Encode bit with probability `p` (1..4095, probability of a **1**):
  `mid = (range >> 12) · p`; bit 1 → `range = mid`; bit 0 → `low += mid, range −= mid`;
  while `range < 2^24`: `range <<= 8`, shift-low.
- Shift-low: if `(uint32)low < 0xFF000000` or `low ≥ 2^32`: output `cache + (low>>32)`
  then `0xFF + (low>>32)` for each pending byte, `cache = (low >> 24) & 0xFF`, pending
  reset; count one more pending byte; `low = (low << 8) mod 2^32`.
- Flush: five shift-lows. Decoder: `code` = first five bytes; decode bit:
  `mid = (range >> 12)·p; bit = code < mid`; then the same range update (`code −= mid`
  on 0); normalise by shifting in bytes while `range < 2^24`.
- Direct bit (Wav16 only): `range >>= 1`; 1 → `low += range`; normalise.

The decoder consumes exactly as many bytes as the encoder wrote, so a block ends there.

### Tables

- **Squash** (stretched −2048..2047 → probability): 33 stems
  `1,2,4,6,19,25,38,71,82,128,210,323,497,778,1142,1526,2047` and their mirror
  `4096 − stem` for the upper half; position `x + 2048` interpolates between stems
  `pos/128` and `pos/128 + 1` with weight `pos mod 128`, rounding half up; clamped to
  1..4095. A 32 768-entry lookup serves arbitrary inputs: index `x & 0x7FFF`, read as
  `i − 32768` when `i ≥ 16383` (so **16383 maps like −16385, to 1**), ≤ −2048 → 1,
  ≥ 2048 → 4095.
- **Stretch** (probability 0..4095 → −2048..2047): for x from −2048 upward, every
  probability from the previous squash value up to (excluding) `squash(x)` stretches to
  x; the rest to 2047.
- **Bit-history states**: 256 states with a fixed transition table (in `McmTables`),
  renumbered: a depth-first walk from state 0 (zero branch first) numbers states 1, 2, …
  in visiting order; then new state 0 copies new state 1's transitions. Hash table bytes
  are these states; 0 is "never seen".
- **Initial probabilities**: ten 256-entry rows (in `McmTables`); probability map `j`
  starts from row `min(j, 9)`.

### Components

- **Probability map** (one per input, twice: non-match and match sets, `inputs + 1` maps
  each): 256 entries of `{p31: 31-bit probability, learn, st: stretched value}`.
  Initial `p31 = row << 19`, `st = stretch(row)`. Update with bit `b`:
  `st = stretch(p31 >> 19)` **of the value before the update**, then
  `p31 += ((b << 31) − p31) >> learn` (arithmetic shift). The model reads `st`, i.e. one
  update behind.
  Learn rates by model kind (assigned to map `i` by the kind of the i-th context in the
  *hash order* below, unfilled slots take order-0's): text — order0/1/2 24, order3 11,
  order4 12, order5 9, order7 9, bracket 15, interval 9, interval2 24, word1 20,
  special-char 13, others 9; binary (and simple) — order0 10, order1 8, order2 8,
  order3 7, order4 6, sparse34 8, sparse2 8, sparse3 9, sparse4 6, interval2 7, others 7.
- **Mixer** array of `256 << (4 + mem)` mixers, each `inputs` int32 weights, a skew,
  and a learn count. Initial weight `((41 << 17) / inputs) / 16`, skew 0, learn 0.
  Output `(skew + Σ p_i·w_i) >> 17` (products 32-bit, sum 64-bit).
  Update with `pr` (the squashed output) and bit: `err = ((b << 12) − pr) · rate[learn]`;
  if `|err| > 600`: `w_i += (err · p_i) >> 16`, `skew += 2·err`, `learn += learn < 255`,
  and the update is *taken*. `rate[i] = 31 + 2500/(3+i)` for text, `60 + 3100/(3+i)`
  otherwise.
- **SSE** (APM): `257·256` contexts × 33 cells of `{p: 12-bit << 19 | count(8)}`, cell `k`
  starting at `squash(min(128k, 4096) − 2048)` with count 64. Lookup of stretched `s`
  (clamped −2048..2047), `q = s + 2048`: cells `ctx·33 + q/128` and next, interpolated
  `(lo·(128 − q%128) + hi·(q%128)) >> 7`, then `+1` if 0; the cell nearer `q` is the one
  later updated: `rate = 2 + count/32`; `p += ((target − p) >> rate) & ~0xFF` with target
  0xFF for 0 and 0x7FFFFFFF for 1; `count += count < 255`.
- **Match model**: history buffer `(256 KiB << mem)` bytes (power of two), match table of
  `size/2` 32-bit entries. Each byte: `hash ^= previous byte`; slot = `hash & mask`.
  If a match is running: length += 1 (cap 80), position += 1. Else if the slot's bits
  above the buffer mask equal the hash's: compare the 4 bytes *before* the stored position
  with the 4 before the current one (a 32-bit load that may run into the buffer's 4-byte
  zero padding past its end); equal → match of length 4 at the stored position. Then the
  slot gets `current position & buffer mask | hash bits above the mask`. The expected byte
  is the one after the match position. Per-length predictors: `256` contexts (low byte
  of the interval model) × 162 models (two per length 0..80, the expected bit selecting
  which), 22-bit probability, initialised `2048/len` and `4095 − 2048/len` for
  `len = 8 + k/2`; read through stretch; updated with shift 7 after every non-LZP bit;
  a mispredicted bit ends the match. The model's next hash is set from the context hash
  after each byte (see below).
- **Word model** (on reordered bytes): letters map to 0–25 (case folded), then bytes
  6, 38, 92, 3 and finally every unmapped byte ≥ 128 to the following numbers; others are
  separators. In a word: `h1 = h1·43 + code`, `h2 = h1·24`, length += 1 (cap 16). At a
  separator after a word: `prev = rotl32((uint32)hash·21, 14)`, reset `h1 = 0x1F20239A,
  h2 = 0xBE5FD47A`, length 0. `hash = (uint32)(t ^ t>>4)` with `t = h1·15 ^ h2·41`
  (64-bit). An escape byte (reordered 0x03) followed by a byte ≥ 128 feeds that byte
  twice.
- **Bracket model**: a 256-deep stack (wraps to empty when full) pushing `[ ( { <` and
  `* > $ \ " H \n` (reordered); a closer pops and empties the stack if it did not match.
  Hash `(last byte << 9) | (stack top << 1) | (bytes since a bracket event > 0)`.
- **Interval models**: `interval = interval << 4 | map1[c]`, `interval2 = interval2 << 4
  | map2[c]`, `small = small·8 + small_map[c]`. Text uses three published masks (in
  `McmTables`); binary computes them from the byte's rank `i` in the binary order:
  `small = (i<1)+(i<32)+(i<64)+(i<128)+(i<255)+(i<142)+(i<138)`,
  `map = small + (i<140)+(i<137)+(i<97)+(i<100)+(i<128)+(i<48)+(i<252)+(i<99)`, map2 = map.

### Contexts

`HashFunc(a, b)`: `b += a; b += rotl32((uint32)(b·7), 11); return (uint32)(b ^ (b >> 13))`
with `b` 64-bit. `hashify(h)`: `h ^= h>>9; h ^= h·6937; h += h>>13` (64-bit), keep the low
32 bits. A hashed context's base index is `(hash & (2 MiB << mem) − 1) + 34 079 232`.
Direct tables precede it, 512 bytes per context: order0 at 0, order1 at 512 + p0·512,
order2 at 131 584 + (p1p0)·512, sparse2/3/4 at 33 686 016 / 33 817 088 / 33 948 160 + byte·512
(p1, p2, p3). The table is `hash size + 34 079 232 + 65 536` bytes, zeroed.

Contexts are generated in this fixed **hash order** — only the profile's enabled ones:

    order0 · special-char (constant 123 119 912) · order1 · sparse2 · sparse3 · sparse4 ·
    sparse23 HashFunc(p2, HashFunc(p1, 0x37220B98)) · sparse34 HashFunc(p3, HashFunc(p2,
    0x651A833E)) · order2 · order 3..max: h = HashFunc(byte[pos − order], h) (starting
    from h = HashFunc(p1p0·3, 0xFFFFFFFF_D8933E53)) for every order up to the profile's
    maximum, emitted for enabled orders · word1 (word hash, xor prev hash while the word
    is shorter than 2) + 99 912 312 · word2 prev + 111 992 · word12 hash^prev + 5 111 321 ·
    interval hashify(interval & mask) + 0x2FA33ED7 · interval2 hashify(interval2 &
    0xFFFFFFFF) + 2 716 049 183 · bracket hashify(hash + 739 108 107) + 0x20019412

(`0xFFFFFFFF_D8933E53` is `0x4EC457C1 · 19` evaluated in 32-bit signed arithmetic and
sign-extended; `0x2FA33ED7` is `7 · 0x97654321` mod 2³². Interval mask: 2⁴⁹−1 text,
2³²−1 otherwise. The running `h` after the loop becomes the match model's hash.)

Profiles — the first `inputs` entries of each list are enabled (duplicates are harmless):

| Profile | Order | Match order | Min LZP | Fast path |
|---|---|---|---|---|
| text | o4 o2 bracket interval o3 o5 word1 interval2 o0 o1 sparse2 sparse3 sparse4 sparse34 | 7 | 12 | – |
| text, in a match | o4 o2 bracket o7 interval special word1 o5 o2 o1 sparse2 sparse3 sparse4 sparse34 | 7 | | |
| binary | o1 o3 sparse34 sparse2 interval2 o2 o4 sparse3 sparse4 o0 o0 | 6 | 0 | after 25 000 unmatched bytes |
| binary, in a match | o1 o2 sparse34 o4 interval2 sparse2 sparse3 sparse4 o0 o9 | 6 | | |
| simple | uses *text* as its non-match profile and o0 o1 o2 o3 o4 o6 o7 o8 o9 in a match | 8 | 12 | – |

"Match order" n means the match model hashes orders 3..n−1; the context loop runs to
`max(highest enabled order, n − 1)`. Min LZP is 101 (never) when LZP is off. Text
interval-mixer mask `2^(3+mem) − 1`, binary/simple `2^(2+mem) − 1`.

### Per byte

    if the profile's match order ≠ 0: update the match model; if it has a match:
        reset the miss counter, select its context set by interval & 0xFF,
        expected = history[match position + 1]
    h = HashFunc(p1p0·3, 0xFFFFFFFF_D8933E53)
    if no match: miss counter += 1; if it reaches the fast-path threshold → fast path
    pick the match or non-match profile and its probability-map set; generate contexts;
    match model hash = h
    mixer context = binary/simple: ((interval & mask) << 1) | (match > 0)
                    text:          ((small & mask) << 1) | (match > 0 or word length > 6)
    if match length ≥ min LZP: code one bit "byte == expected" (LZP bit, below);
        a 1 ends the byte
    code the 8 bits (normal bits; *with SSE* if an LZP bit was coded for this byte)
    then: word model, history, interval models, last bytes, bracket model ← byte

### Per bit

The 8 bits of a byte walk a 255-node tree in two nibbles: node `n` starts at 0, becomes
`2n + 1 + bit` inside the first nibble; after the 4th bit it is `15·(high nibble + 1)`,
and inside the second nibble `15·(hi+1) + s` with `s` advancing like the first nibble.

For input `i`, the state byte is `table[context_i ^ x]` with `x` = node for normal bits,
`256 + expected` for the LZP bit. Input 0 is replaced by the match model's stretched
prediction for the expected bit when a match is running (by its "expected bit = 1"
prediction for the LZP bit). Every other input contributes `st` of probability map
`i` (of the active set) at its state.

    mixer = mixers[(mixer context << 8) + node]   (node 0 for the LZP bit)
    s = mixer output;  pr = squash(s)
    LZP bit:      p = SSE2(clamp(s), 256·(1 + expected) + match length)
    normal + SSE: p = SSE1(clamp(s), 256·(1 + expected) + node)
    otherwise:    p = pr
    code the bit with p (+1 if 0)
    if the mixer update (with pr) was taken: each input whose state was read gets its
        probability map updated (input 0 only if no match and not LZP) and its state
        advanced through the transition table
    update the SSE that was used (always); if not LZP: match model bit update

### Fast path (binary only)

After 25 000 consecutive unmatched bytes, a byte is coded with three states only
(order 2 / order 1 / order 0 tables) and a 4096-entry direct mixer: each state's initial
probability (row 0, stretched) gives a 4-bit index `(st + 2048) >> 8`; the triple picks a
22-bit probability (`p += ((b << 30) − p) >> 10`), clamped to 1..4095. Bit nodes run
1..15 per nibble; the second nibble's tables are offset by `15 + 15·hi`. The match
model hash is still advanced (orders 3..match order − 1).

**Measured**: mcm 0.84's *decoder* returns `0x10 | low nibble` from this path instead of
the byte, so it cannot restore binary data with long unmatched stretches (a 60 000-byte
random file differs from byte 24 999 on). The encoder is consistent; our decoder returns
the coded byte and therefore restores such archives correctly.

### Memory

`mem` 0–11: hash `2 MiB << mem` + 32.6 MiB direct tables; history `256 KiB << mem`
(+ twice that for the match table); `256 << (4 + mem)` mixers. Levels 10 and 11 need a
hash table beyond 2 GiB and are refused; the metadata always uses level 6 (~200 MiB).

## Wav16

The analyzer recognises `RIFF` + chunk size (LE) + `WAVE` + `fmt ` + size 16 or 18 +
format 1 + 2 channels, then scans up to five sub-chunks for `data`; when that chunk's size
is below the RIFF size, the wave run starts at the data and is **RIFF-size bytes** long
(clipped at EOF). Its compressor: samples are little-endian int16 pairs (an odd last byte
is dropped); per 4096-sample block, `s[i] −= s[i−2]` for i ≥ 2 (in descending order),
error `e = −s[i]` (16-bit); per sample a direct bit `e >> 15`, `e = ~e` if set, then the
low 15 bits: 11 modelled bits (context `channel·2048 + node`, node from 1, 22-bit
probability, shift 6, clamped to 1..4095) and 4 direct bits.

**Measured**: mcm 0.84's Wav16 *decoder* does not invert this (it never reads the sign
bit and predicts `2·last − last2`); a stereo WAV comes back wrong and the process hangs.
Our reader inverts the encoder.

## Where our writer departs from mcm, and why

The writer reproduces mcm's bytes (file order, analyzer runs, dictionary — including the tie
order of the GCC runtime's unstable `std::sort` — filters and coders). It departs in exactly two
places, both to keep every archive extractable by mcm 0.84 itself:

- A binary block whose coding entered the fast path (25 000 unmatched bytes) is coded again with
  the text model set, or stored when that is not smaller; the block's profile or compressor byte
  says so, and mcm reads either.
- Wave runs are not detected; stereo PCM stays in the binary block (subject to the rule above).

Names are written as UTF-8. mcm's Windows build writes the ANSI code page's bytes, the Linux
build the bytes it was given; our reader takes a name as UTF-8 when it is valid UTF-8 and as
Latin-1 otherwise.

## Not reproduced

- The word counter discards rare words only when its 256 MiB arena fills (about 8 million
  distinct words); our builder never does, so dictionaries of such inputs could differ.
- `-filter=` / `-lzp=` overrides are not offered by the writer (forcing the dictionary filter on
  two blocks crashes mcm); the reader decodes whatever combination is stored.
