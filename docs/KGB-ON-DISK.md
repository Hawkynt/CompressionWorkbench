# KGB Archiver on disk

The layout of the archives the console KGB Archiver 1.0 (Tomasz Pawlak, 2005-2006; the POSIX
port by Slawek, packaged by Debian as `kgb`) writes, and the PAQ6-derived model its arithmetic
coder runs on. `FileFormat.Kgb` implements both directions from this document.

## 🔎 Provenance

- **Licence.** KGB Archiver and PAQ6 (Matt Mahoney, 2004) are GPL-2.0-or-later, so this is rung 2
  of the sourcing ladder: the Debian source package `kgb_1.0b4+ds` (upstream
  `kgb_arch_posix_by_slawek.cpp` and Debian's patches, which fix the integer widths to 32 bits)
  was read to write this document, and the implementation was written from this document. The
  counter state table and the other constants below are copied as the format requires.
- **Oracle.** Debian's `kgb` 1.0b4+ds-14 (Ubuntu 24.04, run in WSL): it must extract what we
  write, and we must extract what it writes, at every memory level.

## 🗂️ Archive

All text is ASCII with CR LF line ends:

```
KGB_arch -M\r\n                  M = memory level 0..9 (the model size)
<size>\t<name>\r\n               one line per file, size in decimal
...
1A 0C 00                         end of header
<arithmetic-coded data>           every file's bytes, concatenated, one coded stream
```

A reader takes header lines up to the first line shorter than two characters (the line
holding `1A`), then expects `0C 00`. Names may hold `/`; KGB refuses names containing `../` or
`..\`. The coded stream has no length of its own: the decoder produces the sum of the sizes.

## 🧮 Arithmetic coder

32-bit range `[x1, x2]`, starting at `[0, 0xFFFFFFFF]`. For each bit, with `p` the model's
probability that the bit is 1 scaled to 0..4095:

```
d = x2 - x1
mid = x1 + (d >> 12) * p            when d >= 0x4000000
mid = x1 + (((d >> 6) * p) >> 6)    when 0x100000 <= d < 0x4000000
mid = x1 + ((d * p) >> 12)          otherwise
bit 1: x2 = mid      bit 0: x1 = mid + 1
while the top bytes of x1 and x2 agree: emit (or consume) that byte,
  x1 <<= 8, x2 = (x2 << 8) | 0xFF
```

The decoder keeps `x`, the next four stream bytes (zero past the end), and decodes 1 when
`x <= mid`. At the end the encoder emits the top byte of `x2` once more after the shifting loop.
Bits are coded most significant first; the model is updated after each bit, before the
shifting loop.

## 🎲 Pseudo-random numbers and hashes

One generator serves the whole run, in a fixed order of calls. It keeps 55 32-bit values,
`t[0] = 123456789`, `t[1] = 987654321`, `t[j] = t[j-1]*11 + (t[j-2]*19)/16` (32-bit wrapping
arithmetic), and an index starting at 0. Each draw advances the index (wrapping 55 to 0), then
replaces `t[i]` by `t[i] ^ t[i-24]` when `i >= 24`, else by `t[i] ^ t[i+31]`, and returns it.

The first 2048 draws fill eight 256-entry hash tables, table 7 first, each from index 0 up
(table 0 entry 255 is then 3610026313). A hash of one to four bytes is the 32-bit sum of
table `k` at byte `k`: `H(a) = T0[a]`, `H(a,b) = T0[a] + T1[b]`, and so on. Every hash argument is
reduced to its low 8 bits first.

Later draws come only from counters (below) and the mixers' weight updates, in the order the
model performs them.

## 🔢 Counters

A counter is one byte, a state of the 255-entry table below. Each state gives the two
counts `n0`, `n1` the counter contributes, the next states after a 0 (`s00` deterministic,
`s01` incremented) and after a 1 (`s10`, `s11`), and the probabilities `p0`, `p1` (scaled by
2^32) of taking the incremented state. Below state 208 the incremented state is always taken;
from 208 on a draw `r` is made and the incremented state is taken when `r < p` (for a 1 bit:
`s11` when `r < p1`, else `s10`).

The table (state: n0 n1 s00 s01 s10 s11 p0 p1; `F` = 4294967295, `Q` = 1073741823,
`E` = 536870911, `S` = 134217727, `T` = 138547332):

```
  0:   0   0   0   2   0   1 F F        1:   0   4   1   4   1   3 F F
  2:   4   0   2   5   2   4 F F        3:   0   8   1   4   3   6 F F
  4:   1   1   4   8   4   7 F F        5:   8   0   5   9   2   4 F F
  6:   0  12   1   4   6  10 F F        7:   1   2   4   8   7  11 F F
  8:   2   1   8  13   4   7 F F        9:  12   0   9  14   2   4 F F
 10:   0  16   3   7  10  15 F F       11:   1   3   4   8  11  16 F F
 12:   1   1   8  13   7  11 F F       13:   3   1  13  19   4   7 F F
 14:  16   0  14  20   5   8 F F       15:   0  20   3   7  15  21 F F
 16:   1   4   7  12  16  22 F F       17:   1   1   8  13  11  16 F F
 18:   1   1  13  19   7  11 F F       19:   4   1  19  26   8  12 F F
 20:  20   0  20  27   5   8 F F       21:   0  24   6  11  21  28 F F
 22:   1   5   7  12  22  29 F F       23:   1   2  12  18  16  22 F F
 24:   1   1  13  19  11  16 F F       25:   2   1  19  26  12  17 F F
 26:   5   1  26  34   8  12 F F       27:  24   0  27  35   9  13 F F
 28:   0  28   6  11  28  36 F F       29:   1   6  11  17  29  37 F F
 30:   1   2  12  18  22  29 F F       31:   1   1  18  25  16  22 F F
 32:   1   1  19  26  17  23 F F       33:   2   1  26  34  12  17 F F
 34:   6   1  34  43  13  18 F F       35:  28   0  35  44   9  13 F F
 36:   0  32  10  16  36  45 F F       37:   1   7  11  17  37  46 F F
 38:   1   3  17  24  29  37 F F       39:   1   1  18  25  22  29 F F
 40:   1   1  25  33  23  30 F F       41:   1   1  26  34  17  23 F F
 42:   3   1  34  43  18  24 F F       43:   7   1  43  53  13  18 F F
 44:  32   0  44  54  14  19 F F       45:   0  36  10  16  45  55 F F
 46:   1   8  16  23  46  56 F F       47:   1   3  17  24  37  46 F F
 48:   1   2  24  32  29  37 F F       49:   1   1  25  33  30  38 F F
 50:   1   1  33  42  23  30 F F       51:   2   1  34  43  24  31 F F
 52:   3   1  43  53  18  24 F F       53:   8   1  53  61  19  25 F F
 54:  36   0  54  62  14  19 F F       55:   0  40  15  22  55  63 F F
 56:   1   9  16  23  56  64 F F       57:   1   4  23  31  46  56 F F
 58:   1   2  24  32  37  46 F F       59:   2   1  43  53  24  31 F F
 60:   4   1  53  61  25  32 F F       61:   9   1  61  69  19  25 F F
 62:  40   0  62  70  20  26 F F       63:   0  44  15  22  63  71 F F
 64:   1  10  22  30  64  72 F F       65:   1   4  23  31  56  64 F F
 66:   1   2  31  40  46  56 F F       67:   2   1  53  61  32  40 F F
 68:   4   1  61  69  25  32 F F       69:  10   1  69  77  26  33 F F
 70:  44   0  70  78  20  26 F F       71:   0  48  21  29  71  79 F F
 72:   1  11  22  30  72  80 F F       73:   1   5  30  39  64  72 F F
 74:   1   3  31  40  56  64 F F       75:   3   1  61  69  32  40 F F
 76:   5   1  69  77  33  41 F F       77:  11   1  77  85  26  33 F F
 78:  48   0  78  86  27  34 F F       79:   0  52  21  29  79  87 F F
 80:   1  12  29  38  80  88 F F       81:   1   5  30  39  72  80 F F
 82:   1   3  39  49  64  72 F F       83:   3   1  69  77  41  50 F F
 84:   5   1  77  85  33  41 F F       85:  12   1  85  91  34  42 F F
 86:  52   0  86  92  27  34 F F       87:   0  56  28  37  87  93 F F
 88:   1  13  29  38  88  94 F F       89:   1   6  38  48  80  88 F F
 90:   6   1  85  91  42  51 F F       91:  13   1  91  97  34  42 F F
 92:  56   0  92  98  35  43 F F       93:   0  60  28  37  93  99 F F
 94:   1  14  37  47  94 100 F F       95:   1   6  38  48  88  94 F F
 96:   6   1  91  97  42  51 F F       97:  14   1  97 103  43  52 F F
 98:  60   0  98 104  35  43 F F       99:   0  64  36  46  99 105 F F
100:   1  15  37  47 100 106 F F      101:   1   7  47  58  94 100 F F
102:   7   1  97 103  52  59 F F      103:  15   1 103 109  43  52 F F
104:  64   0 104 110  44  53 F F      105:   0  68  36  46 105 111 F F
106:   1  16  46  57 106 112 F F      107:   1   7  47  58 100 106 F F
108:   7   1 103 109  52  59 F F      109:  16   1 109 113  53  60 F F
110:  68   0 110 114  44  53 F F      111:   0  72  45  56 111 115 F F
112:   1  17  46  57 112 116 F F      113:  17   1 113 119  53  60 F F
114:  72   0 114 120  54  61 F F      115:   0  76  45  56 115 121 F F
116:   1  18  56  65 116 122 F F      117:   1   8  57  66 112 116 F F
118:   8   1 113 119  60  67 F F      119:  18   1 119 125  61  68 F F
120:  76   0 120 126  54  61 F F      121:   0  80  55  64 121 127 F F
122:   1  19  56  65 122 128 F F      123:   1   9  65  74 116 122 F F
124:   9   1 119 125  68  75 F F      125:  19   1 125 131  61  68 F F
126:  80   0 126 132  62  69 F F      127:   0  84  55  64 127 133 F F
128:   1  20  64  73 128 134 F F      129:   1   9  65  74 122 128 F F
130:   9   1 125 131  68  75 F F      131:  20   1 131 137  69  76 F F
132:  84   0 132 138  62  69 F F      133:   0  88  63  72 133 139 F F
134:   1  21  64  73 134 140 F F      135:   1  10  73  82 128 134 F F
136:  10   1 131 137  76  83 F F      137:  21   1 137 143  69  76 F F
138:  88   0 138 144  70  77 F F      139:   0  92  63  72 139 145 F F
140:   1  22  72  81 140 146 F F      141:   1  10  73  82 134 140 F F
142:  10   1 137 143  76  83 F F      143:  22   1 143 147  77  84 F F
144:  92   0 144 148  70  77 F F      145:   0  96  71  80 145 149 F F
146:   1  23  72  81 146 150 F F      147:  23   1 147 151  77  84 F F
148:  96   0 148 152  78  85 F F      149:   0 100  63  72 149 153 F F
150:   1  24  80  89 150 154 F F      151:  24   1 151 155  85  90 F F
152: 100   0 152 156  70  77 F F      153:   0 104  63  72 153 157 F F
154:   1  25  72  81 154 158 F F      155:  25   1 155 159  77  84 F F
156: 104   0 156 160  70  77 F F      157:   0 108  63  72 157 161 F F
158:   1  26  72  81 158 162 F F      159:  26   1 159 163  77  84 F F
160: 108   0 160 164  70  77 F F      161:   0 112  63  72 161 165 F F
162:   1  27  72  81 162 166 F F      163:  27   1 163 167  77  84 F F
164: 112   0 164 168  70  77 F F      165:   0 116  63  72 165 169 F F
166:   1  28  72  81 166 170 F F      167:  28   1 167 171  77  84 F F
168: 116   0 168 172  70  77 F F      169:   0 120  63  72 169 173 F F
170:   1  29  72  81 170 174 F F      171:  29   1 171 175  77  84 F F
172: 120   0 172 176  70  77 F F      173:   0 124  63  72 173 177 F F
174:   1  30  72  81 174 178 F F      175:  30   1 175 179  77  84 F F
176: 124   0 176 180  70  77 F F      177:   0 128  63  72 177 181 F F
178:   1  31  72  81 178 182 F F      179:  31   1 179 183  77  84 F F
180: 128   0 180 184  70  77 F F      181:   0 132  63  72 181 185 F F
182:   1  32  72  81 182 186 F F      183:  32   1 183 187  77  84 F F
184: 132   0 184 188  70  77 F F      185:   0 136  63  72 185 189 F F
186:   1  33  72  81 186 190 F F      187:  33   1 187 191  77  84 F F
188: 136   0 188 192  70  77 F F      189:   0 140  63  72 189 193 F F
190:   1  34  72  81 190 194 F F      191:  34   1 191 195  77  84 F F
192: 140   0 192 196  70  77 F F      193:   0 144  71  80 193 197 F F
194:   1  35  72  81 194 198 F F      195:  35   1 195 199  77  84 F F
196: 144   0 196 200  78  85 F F      197:   0 148  71  80 197 201 F F
198:   1  36  80  89 198 202 F F      199:  36   1 199 203  85  90 F F
200: 148   0 200 204  78  85 F F      201:   0 152  71  80 201 205 F F
202:   1  37  80  89 202 206 F F      203:  37   1 203 207  85  90 F F
204: 152   0 204 208  78  85 F F      205:   0 156  71  80 205 209 F F
206:   1  38  80  89 206 210 F F      207:  38   1 207 211  85  90 F F
208: 156   0 208 212  78  85 F F      209:   0 160  71  80 209 215 F Q
210:   1  39  80  89 210 213 F F      211:  39   1 211 214  85  90 F F
212: 160   0 212 216  78  85 Q F      213:   1  40  80  89 213 217 F Q
214:  40   1 214 218  85  90 Q F      215:   0 176  71  80 215 219 F Q
216: 176   0 216 220  78  85 Q F      217:   1  44  80  89 217 221 F Q
218:  44   1 218 222  85  90 Q F      219:   0 192  71  80 219 223 F E
220: 192   0 220 224  78  85 E F      221:   1  48  80  89 221 225 F E
222:  48   1 222 226  85  90 E F      223:   0 224  79  88 223 227 F E
224: 224   0 224 228  86  91 E F      225:   1  56  88  95 225 229 F E
226:  56   1 226 230  91  96 E F      227:   0 256  87  94 227 231 F S
228: 256   0 228 232  92  97 S F      229:   1  64  94 101 229 233 F S
230:  64   1 230 234  97 102 S F      231:   0 384  93 100 231 235 F S
232: 384   0 232 236  98 103 S F      233:   1  96 100 107 233 237 F S
234:  96   1 234 238 103 108 S F      235:   0 512 105 112 235 239 F S
236: 512   0 236 240 110 113 S F      237:   1 128 112 117 237 241 F S
238: 128   1 238 242 113 118 S F      239:   0 640 111 116 239 243 F S
240: 640   0 240 244 114 119 S F      241:   1 160 116 123 241 245 F S
242: 160   1 242 246 119 124 S F      243:   0 768 115 122 243 247 F S
244: 768   0 244 248 120 125 S F      245:   1 192 122 129 245 249 F S
246: 192   1 246 250 125 130 S F      247:   0 896 121 128 247 251 F T
248: 896   0 248 252 126 131 T F      249:   1 224 128 135 249 253 F T
250: 224   1 250 254 131 136 T F      251:   0 1020 127 134 251 251 F 0
252: 1020  0 252 252 132 137 0 F      253:   1 255 134 141 253 253 F 0
254: 255   1 254 254 137 142 0 F
```

## 📜 History buffer

The model sees the input through a ring buffer of `2^(19 + M - [M >= 6])` bytes. Position `pos`
counts whole bytes; the byte at `pos` is the partial byte, holding the bits seen so far behind a
leading 1 (1 at a byte boundary). `c(k)` is the byte `k` back from the partial one (`c(0)` the
partial byte, `c(1)` the last whole byte), `B[a]` the byte at absolute position `a` (both taken
modulo the buffer size). For every byte value the buffer also remembers the positions of its
last four occurrences (`last(v, 0)` most recent; all start at 0). `lo` is the current nibble so
far behind a leading 1 (1..15), `hi` the previous nibble (0..15).

Appending a bit `y`: the partial byte becomes `2·b + y`; after the eighth bit, the occurrence
list of the finished byte shifts and records the position, `pos` advances and the new partial
byte is 1. The nibble register `lo` likewise doubles plus `y`; on reaching 16 it moves to `hi`
(minus 16) and restarts at 1.

`text` is true when the fourth most recent 0 byte lies before the fourth most recent space
(`last(0, 3) < last(32, 3)`).

## 🗃️ Context tables

**Direct tables.** Order 0: 256 counters indexed by `c(0)`; order 1: 65536 counters indexed by
`c(0) + 256·c(1)`.

**Nibble hash table** of `2^n` bytes (n the table's size exponent): `2^(n-4)` elements of 16
bytes (`2^1` elements when `n <= 4`), each a checksum byte and 15 counters for the 15 partial
nibbles 1..15. Looking up a 32-bit context hash `h`: checksum `(h >> 24) ^ h` (low byte); the
bucket is the four elements from `((h >> (32 - n')) & ~3)`, `n'` = log2 of the element count.
The first element whose checksum matches, or failing that whose first counter is in state 0
(empty, claimed with the checksum), is taken. If none qualifies, a new zeroed element with the
checksum goes to the front: the old front three move back one when the third element's first
counter has a lower `n0 + n1` than the fourth's (the third is dropped), otherwise all four move
back one (the fourth is dropped). A found element that is not at the front moves to the front,
the ones before it moving back one. Moves shift whole 16-byte elements; a counter is addressed
by its position in the table, so a counter located before a move refers afterwards to whatever
element now occupies that position.

**Run table** of `2^n` bytes: `2^(n-1)` (at least 2) two-byte entries `(byte, count)` indexed by
`h >> (32 - (n - 1))`, no collision check.

**Context map** with size exponent `n`: a nibble hash table of `2^n` bytes and, at memory level
5 and above, a run table of exponent `n - 2`. It holds a current context hash `h` (set at byte
boundaries) and eight counter positions:

- *predict* (once per bit, as `write` = a new mixer input or `add` = added to the last one): at
  bit 4 of the byte the hash becomes `h ^ H(hi, h)` and the nibble table is looked up with it
  (at bit 0 the lookup was made when the hash was set); the counter for `lo` is remembered for
  this bit and contributes its `(n0, n1)`. At level 5 and above the run table also contributes:
  when the bits so far agree with the stored byte (`(byte + 256) >> (8 - bit) == c(0)`), it adds
  `(0, count)` if the stored byte's next bit is 1, else `(count, 0)`; nothing otherwise.
- *update(h')* (at byte boundaries, after the byte is complete): at level 5 and above the run
  entry of the old hash learns `c(1)` (empty: byte `c(1)`, count 1; same byte: count + 1 up to
  255; other byte: byte `c(1)`, count 1). Each of the eight remembered counters, in bit order,
  then learns its bit of `c(1)`, and the remembered positions are cleared. Then `h = h'`, the
  run entry is selected by `h'` and the nibble table is looked up with `h'`.

## 🧠 Model

Each bit, after the coder used the current probability: the mixers learn the bit, the history
appends it, every submodel updates and makes its predictions (mixer inputs `(n0, n1)` in the
order below), the mixers predict, and SSE refines the result. Before the first bit the
probability is 2048 and no inputs exist. "At a byte boundary" means the partial byte is empty
(bit 0).

Memory level `M` selects the parts: char model always; match model `M >= 2`; exe model
`M >= 3`; record, sparse, analog and word models `M >= 4`; two mixers `M >= 5` (else one); SSE
`M >= 1`; run tables in context maps `M >= 5`. A map of size exponent 0 is never used.

Inputs, in order:

1. **Default:** `(1, 1)`.
2. **Char model:** before predicting, the two direct counters used for the previous bit learn
   it. At a byte boundary the order hashes shift: `x9 .. x1` become `x(k) = x(k-1) ^ H(c(1), k)`
   (`x0 = 0`), and maps 2..9 are updated with `x2 .. x9` (maps 5 `M >= 1`, 6-7 `M >= 3`, 8-9
   `M >= 5`). Then: order-0 counter (write), order-1 counter (write), map 2 write, map 3 write,
   map 4 write, map 5 add, map 6 write, map 7 add, map 8 write, map 9 add. Size exponents:
   map 2 `M+15`, map 3 `M+17`, map 4 `M+18`, maps 5-7 `M+18`, maps 8-9 `M+18-[M>=6]`.
3. **Match model** (index of `2^(17+M-[M>=6])` positions, four match slots `(begin, end)`): at
   a byte boundary two rolling hashes advance, `ha = ha·908754512 + c(1) + 1` and
   `hb = hb·91368434 + c(1) + 1`; the index slot is `ha >> (32 - bits)`, or `hb >> (32 - bits)`
   when `ha >> 28 == 0`. Every active slot whose predicted byte `B[end]` equals `c(1)` advances
   `end`. Then the first empty slot (if any) takes the index entry, unless that value equals
   some slot's `end` (including 0); taking it, `begin` walks back from `end` while
   `begin > 0`, the walking current position `q` (starting at `pos`) is `> 0`,
   `begin != q + 1` and `B[begin-1] == B[q-1]`; if nothing matched, the slot stays empty. The
   index entry is then set to `pos`. Each bit, a slot whose `B[end]` disagrees with the bits so
   far (`(B[end] + 256) >> (8 - bit) != c(0)`) is emptied. Each active slot contributes
   `w = min(511, (end - begin)² / 4)` to `n1` or `n0` by the next bit of `B[end]`; one write.
4. **Record model** (maps of exponent `16+M-[M>=6]`; two cycle lengths `r1 = 2`, `r2 = 3`): at a
   byte boundary, with `v = c(1)` and `d1, d2, d3` the gaps between its last four occurrences:
   when `d1 > 1` and all three are equal, `d1 == r1` swaps `r1, r2`, otherwise `d1 != r2` makes
   `r1 = r2, r2 = d1`. With `a <= b` the two lengths in order, the maps are updated with
   `H(c(a), c(2a), a)`, `H(c(1), c(a), a)`, `H(c(a), pos mod a)`, `H(c(b), c(2b), b)`,
   `H(c(1), c(b), b)`. Five writes.
5. **Sparse model** (exponent `M+15-[M>=6]`): `H(c1,c3)`, `H(c1,c4)`, `H(c1,c5)`, `H(c1,c6)`,
   `H(c2,c3)`, `H(c2,c4)`, `H(c3,c4)`, `H(c1, g)`, `H(c1, c2, g)` with
   `g = min(255, pos - last(c(1), 2))`. Nine writes.
6. **Analog model** (exponent `M+13`; `m3` counts byte boundaries modulo 3, starting at 0 and
   advanced before use): `H(c2/4, c4/4, pos mod 2)`, `H(c2/16, c4/16, pos mod 2)`,
   `H(c2/4, c4/4, c8/4, pos mod 4)`, `H(c3, c6/4, m3)`, `H(c1/16, c2/16, c3/4, m3)`,
   `H(c1/2, c2/8, c3/32)`, `H(c216, c432)`; write, add, add, write, add, write, write.
7. **Word model** (exponent `M+17-[M>=6]`): at a byte boundary, with `v = c(1)`: if `v > 32`,
   `w0 ^= H(w0, v)`; else, when `w0 != 0`, the words shift (`w2 = w1, w1 = w0, w0 = 0`). If `v`
   is an ASCII letter or `v >= 192`, `u0 ^= H(u0, lower(v), 1)` (ASCII lower case); else the
   letter words shift the same way unconditionally. Maps: `w0`, `w1+w0`, `w2+w0`, `u0`,
   `u1+u0`, `u2+u0` (32-bit sums). Six writes.
8. **Exe model** (256 slots of a 32-bit address and an 8-bit count): at a byte boundary, when
   `c(5) == 0xE8` and `c(1)` is 0 or 0xFF, the address
   `a = c4 + c3·2^8 + c2·2^16 + c1·2^24 + pos - 5` goes to slot `a & 255`: the same address there
   increments the count (up to 255), another one replaces it with count 1. Each bit `(n0, n1)`
   starts at 0:
   - when `c(4) == 0xE8` and slot `i = (c3 + pos - 4) & 255` has a count: `r = a_i - pos + 4`;
     if `c(0) == (((r & 0xFF000000) >> 8) + 0x1000000) >> (24 - bit)` and
     `c(1) == (r >> 16) & 255`, the bit `(r >> (31 - bit)) & 1` gets `16·count`;
   - when `c(3) == 0xE8` and slot `(c2 + pos - 3) & 255` has a count: `r = a - pos + 3`; if
     `c(0) == ((r & 0xFF0000) + 0x1000000) >> (24 - bit)` and `c(1) == (r >> 8) & 255`, bit
     `(r >> (23 - bit)) & 1` gets `4·count`;
   - otherwise, when `c(2) == 0xE8` and slot `(c1 + pos - 2) & 255` has a count:
     `r = a - pos + 2`; if `c(0) == ((r & 0xFF00) + 0x10000) >> (16 - bit)`, bit
     `(r >> (15 - bit)) & 1` gets `count`.

   A later case replaces an earlier one's counts. One write.

## ⚖️ Mixers

A mixer has 16 sets of 64 weights (all starting at 1) and up to 64 inputs `(n0_j, n1_j)`.
Prediction with set `s`: `N0 = 1 + Σ n0_j·w_j`, `N1 = 1 + Σ n1_j·w_j` (32-bit wrapping, read as
signed); `S = N0 + N1`; while `S > 488281`: `S /= 4`, `N1 /= 4`; the result is `4095·N1 / S`.

Learning bit `y` (only when there were inputs): `s0 = Σ (w_j + 48)·n0_j`, `s1 = Σ (w_j + 48)·n1_j`
(unsigned 32-bit); when both are positive, with `sy = s1` or `s0` by `y` and `s = s0 + s1`:
`a = (0xFFFFFFFF / sy + (draw & 1023)) >> 10`, then `b = (0xFFFFFFFF / s + (draw & 1023)) >> 10`
(two draws, in that order), and for each input
`dw = (int)(ny_j·a - (n0_j + n1_j)·b + (draw & 255)) >> 8` (unsigned arithmetic, then an
arithmetic shift of the signed value; `ny` = `n1` or `n0` by `y`), `w_j = clamp(w_j + dw, 1, 65535)`.
The inputs are then cleared.

Mixer 1's set is `(c(1) >> 5) + 8·text`; mixer 2 (`M >= 5`) gets the same inputs and uses set
`(c(1) >> 6) + 4·(c(2) >> 6)`; with both, the probability is the mean (integer halving of the
sum). Mixer 1 learns before mixer 2.

## 🎯 SSE

At `M >= 1`: 2048 contexts × 33 bins of `(c1, n)` byte pairs. A stretch table maps
probabilities 0..4095 to 0..1023: `clamp(trunc(64·ln((i + 0.5)/(4095.5 - i)) + 512), 0, 1023)`,
where `trunc` cuts the double value toward zero (it matters for the negative values clamped to 0
only). Initial bins: for `i` from 4095 down to 0,
`p = (stretch(i) + 16) / 32`, `n = min(254, 1 + 4096²/((i+1)(4096-i)))`, `c1 = (i·n + 2048) /
4096`, and bins `p .. previous p - 1` (the first time `p .. 32`) of every context get `(c1, n)`.

A bin predicts `4096·(64·c1 + 1)/(64·n + 2)`. After the mixers predict `q`: context
`(c(0)·4 + c(1)/64)·2 + text`, `t = stretch(q)`, bins `t/32` and `t/32 + 1` interpolate with
weight `t mod 32`: `r = (b0·(32 - wt) + b1·wt)/32`, final probability `(3r + q)/4`. When the bit
is known, both bins (of the context and `t` used) learn it: `c1 += y`, `n += 1`, and when `n`
exceeds 254 both halve.
