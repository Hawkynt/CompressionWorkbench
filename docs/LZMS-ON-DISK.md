# LZMS — on-disk notes

What is written here was derived by having wimlib compress payloads whose LZ77
factorisation was known in advance, and reading the streams it produced. It is
**not** a specification — the format's own analyst calls it "an undocumented
compression format that Microsoft released in 2012 or 2013", and no
specification exists to work from.

No other implementation's source was read. `AGENTS.md` forbids it, and it would
not have been faster: what follows came from designing payloads whose answer was
known before the stream was opened.

These notes exist because the derivation is the expensive part and it was
otherwise going to be lost. They are incomplete, and the gaps are marked.

## This project writes LZMS

`WimWriter` accepts `CompressionLzms` and produces images that `wimlib-imagex
verify` and 7-Zip both read — verify being the one that matters, since it checks
each resource against its SHA-1 and so cannot be satisfied by a chunk that
decodes to something else. The container is the later version with 128 KB
chunks, which is the only one an LZMS resource is ever found in; writing the
ordinary version alongside LZMS would mark the file as ours at a glance.

One thing the writer declines. The decoder runs the format's x86 filter over
every chunk with nothing in the stream to say so, and the last of that filter's
arming rules is not settled — the section on it below says exactly what is and
is not known. On a chunk the filter never touches the question does not arise;
on one it does, `WimWriter` stores the chunk rather than compressing it. That
costs space and nothing else: a WIM carries stored and compressed chunks side by
side as a matter of course and every reader takes both. It is the one place
where our output is larger than wimlib's rather than different from it.

The sections below are the derivation, kept because it was the expensive part.

## How the streams were read

`wimcapture <dir> out.wim --compress=LZMS`, then the resource is pulled out of
the lookup table. Two payload shapes did most of the work:

- **A phrase repeated.** A 45-byte phrase repeated to 20 000 bytes must decode
  to exactly 32 literals and then a match, because the first repeat inside that
  phrase falls at offset 32. Getting exactly 32 zeros and then a one out of a
  candidate range decoder is proof rather than encouragement.
- **`dist` random bytes, then `length` of them copied, then zeros.** The join
  can only be matched `dist` back and the copy is `length` long, so the two are
  independently selectable. Fixing one and sweeping the other is what separated
  the offset field from the length field.

For distances past a few hundred, the literal count has to be kept small or the
literal code is rebuilt mid-stream and everything after it misreads: a short
random seed, a long run of zeros, then the seed again keeps the item count at a
couple of hundred however far apart the two copies are.

## The chunk carries two streams, running in opposite directions

This is the fault that makes every chunk this project writes undecodable
elsewhere: ours has them the other way about.

- **The range-coded stream reads forwards** from the start of the chunk, in
  16-bit little-endian units.
- **The Huffman-coded stream reads backwards** from the end, also in 16-bit
  little-endian units, bits taken most significant first from a 64-bit buffer
  filled as `buf |= word << (48 - bits_held)`.

It is visible without decoding anything. The initial literal code is flat and
eight bits wide — symbol equals byte value — so the tail of a reference chunk
read backwards is the payload's opening text, in order:

    tail read backwards: 54 68 65 20 71 75 69 63 6b 20 62 72 6f 77 6e 20 66 6f 78 20
    as text            : The quick brown fox

## The range decoder

    range = 0xFFFFFFFF
    code  = first unit << 16 | second unit

To decode one bit against a probability `prob`:

    bound = (range >> 6) * prob        (six probability bits)
    code < bound  ->  bit 0, range = bound
    otherwise     ->  bit 1, code -= bound, range -= bound
    while range <= 0xFFFF: range <<= 16, code = code << 16 | next unit

Only the bound is scaled. Narrowing `range` itself before the subtraction wraps
it, and the mistake hides: every bit on the zero path still decodes correctly,
so a stream of literals reads perfectly and nothing goes wrong until the first
match. That is how it survived a first round of checking here.

## The probability model

Each context holds a 64-bit history of the bits most recently coded through it,
and a count of the zeros in that history. Both start from `0x0000000055555555`,
which has sixteen ones — so the initial count is **48**, not the 32 this
project's `LzmsConstants.InitialProb` carries. The probability used is that
count, clamped to the range 1 to 63. Updating with a bit adds the bit leaving
the top of the 64-bit window and subtracts the bit entering the bottom.

The context index is chosen by recent history in the same way:
`state = (state << 1 | bit) & (states - 1)`.

## Items

A main-state bit of **0 is a literal, 1 a match**. Within a match the **offset
symbol precedes the length symbol**: fixing the distance and sweeping the length
leaves the leading bits alone, and fixing the length and sweeping the distance
moves them.

## Offset slots

**Superseded** by the measured tables below, and worth keeping only for the
mistake: what this read as one symbol per distance is the codeword and its extra
bits read as a single number, which does increase by one per distance — inside a
slot because the extra bits count up, and across a slot boundary because the
codeword counts up as the extra bits reset. The concatenated field is monotone in
the distance, so nothing about it reveals where one slot ends and the next
begins.

Slots are **one to one with distance** at least as far as 1000 — a distance of
`d` and one of `d + 1` never share a symbol, and no extra bits follow. The codes
are canonical and grow by exactly one per distance:

    distance   symbol code
    105        3780
    106        3781
    ...
    144        3819

which is `3675 + d` across that run. This contradicts
`LzmsConstants.NumLzOffsetSlots = 799`: an alphabet of 799 cannot give a
thousand distances a symbol each. The real alphabet is larger, and where the
one-to-one region ends is not yet known.

## Writing one

The encoder side is the mirror of the above and is proved as far as the
machinery goes:

- **The range encoder** keeps `low` and `range`, narrows them the same way, and
  emits a unit whenever `range` falls to 0xFFFF or below. Buffering the units
  rather than streaming them lets a carry be added back into what has already
  been produced, which removes the usual cache-and-pending bookkeeping at no
  cost for chunk-sized output. It round-trips with the decoder above.
- **The backward bit writer** collects bits most significant first and lays its
  units at the end of the chunk, the first unit written landing last. Fed the
  literals and match bits taken out of a reference chunk, it reproduces that
  chunk's backward region **byte for byte**.

So the two halves of the machinery are right; what is missing is the content.

## Matches spend range-coded decisions as well as Huffman symbols

A match is not only its offset and length. After the main-state bit, at least
two further bits come from the **range** stream, each from a context of its own
— on the evidence, whether the match is an LZ or a delta match, and whether it
takes an offset of its own or one of the recent ones. In a reference chunk both
decode as zero, which is what a first explicit LZ match should be.

This is why a chunk of literals alone cannot be checked against wimlib: literals
never compress, so the resource is larger than its contents and the image is
refused before the codec is reached. A chunk that compresses needs a match, and
a match needs the two tables below.

**The LZ-or-delta bit has a context of its own: a five-bit history of that same
bit, so 32 probabilities.** One delta in a chunk hides this - a single context
reads it correctly. Two do not. Measured by writing chunks with a growing number
of deltas and asking `wimlib-imagex verify`: a history of *n* bits carries *n+1*
deltas and then fails, and only five carries every length tried, up to thirty.
Indexing by the main state instead fails at once.

## Length slots

**Superseded** in the same way, and by the same monotone field.

The same treatment with the distance held at 64 and the length swept gives the
field that follows the offset. Lengths are **one to one with symbols** as far as
about 24:

    length   field   value
    4        1010110    86
    5        1010111    87
    6        1011000    88
    8        1011010    90
    12       1011110    94
    16       1100010    98
    24       1101010   106

One per length through 16, still one per length to 24, and then slower. That
contradicts `LzmsConstants.LengthBase`, which starts spanning two lengths per
slot from a length of 10. As with the offsets, the real table is finer than the
one this project carries.

A caveat for whoever repeats this: at larger distances a short copy is not worth
a match, and the parser emits it as literals — the first match in the stream is
then the run of zeros that follows, whose bits are constant however the copy's
length is varied. If a length sweep produces the same field for every length,
that is what has happened; shorten the distance or lengthen the copy.

## The container around an LZMS chunk is settled

An image that uses LZMS is **version 3584 with 128 KB chunks** — not 1.13 with
32 KB — and otherwise an ordinary WIM: the same 50-byte lookup entries, the same
resource flags, no chunk table for a resource that fits in one chunk. This is
confirmed rather than assumed: a hand-built image of that shape, wrapped around
a chunk wimlib itself produced, extracts correctly.

That matters more than it sounds. It means only the chunk's contents are
unknown, and it makes the next method possible.

## Reading the format out by flipping bits

`wimapply` extracts a resource whose SHA-1 does not match — it reports the
mismatch and writes the file anyway. So a chunk can be perturbed one bit at a
time and the reference decoder asked what it now sees, which turns the format
from something to be guessed into something to be measured.

On a specimen of 65 literals and one match of 200 bytes at a distance of 64,
flipping each bit of the match's field gives:

    byte 9, bits 0-3   the length: 200 becomes 196, 192, 184, 170
    byte 9, bits 4-6   the distance: becomes 1
    byte 10, all bits  the distance: becomes 75, 78, 96, 238, 264

So within a match the **offset field is read first and the length second**, and
both carry extra bits below their symbol — the length's are worth 4, 8, 16 and
32, so the slot holding 200 is a coarse one. The earlier reading of this — that
the leading bits are the offset — survives, but only on specimens where the item
stream is one match and nothing else; a payload with a trailing run of zeros
produces further matches whose fields sit in front of it and move everything.

## Crafting chunks, and what that settles

Once the container is known and both stream mechanisms work in both directions,
chunks can be **built** rather than only read: sixty-five literals, one match,
and whatever field bits one cares to choose. `wimapply` then says what those
bits meant. A probe costs no measurable time, so the code space can be swept
rather than sampled.

Two constraints make it work. The declared size must exceed the chunk or the
image is refused as uncompressed before the codec is reached; and it must not
exceed what the match supplies, or the decoder reads on into the padding and
fails. Sizing the payload to the match satisfies both.

What this has settled:

- **The literal code is rebuilt after exactly 1024 literals.** A crafted chunk
  of 1024 literals decodes perfectly; 1200 decodes perfectly to literal 1023 and
  diverges at 1024. `LzmsConstants.LiteralRebuildInterval` is right.
- **A chosen offset field reads back as a distance**, so the offset alphabet can
  be enumerated directly: the specimen's own bits give 64, and neighbouring
  codes give 48, 1, and so on.
- **The offset code is not flat.** Unlike the literal code, its codewords come in
  at least three lengths — ten, eleven and twelve bits were all observed — so the
  offset symbol frequencies do not start equal. In one region an eleven-bit
  codeword `v` names distance `805 + v`, one distance per codeword.

## The rebuild is global, and it changes what a codeword means

**Read with care.** The two measurements compared here differ in the resource
size as well as in whether a rebuild had happened, and the offset alphabet is
sized by the resource — so the change in the code that is credited to the
rebuild below has a second candidate explanation that was not controlled for.
That a rebuild happens at 1024 literals is not in doubt; a stream that keeps
writing past it decodes into noise. What it does to the *offset* code is open.

Measured late and worth stating first, because it changes how the table below
should be read: **the same offset bits name different distances depending on how
many literals came before them.** With 65 literals ahead of it, a field of
`011010110…` names a distance of 64; with 1024 literals ahead of it, the same
field names 28, and 64 is named by `100001011` instead.

The literal code is rebuilt after 1024 literals. What this shows is that the
rebuild is not confined to the literal code — the **offset** code is rebuilt at
the same moment, though not one offset symbol has been coded yet.

That has a useful consequence. A rebuild with nothing yet counted gives every
symbol the same weight, so the rebuilt code is flat: six bits, sixty-four
codewords. The table below was measured with 1024 literals ahead of the match,
so it is that flat rebuilt code — which is why it came out so clean, and why an
earlier attempt at the same measurement with fewer literals showed codewords of
several different lengths. The **initial** offset code is not flat; something
gives small distances shorter codewords before any statistics exist.

That is not the whole of it, and the rest explains every disagreement this
derivation has run into. **The offset alphabet is sized by the resource.** Hold
the literals and the field bits fixed and vary only the declared uncompressed
size, and the same bits name a different distance each time:

    declared size  712 -> distance 61
    declared size  912 -> distance 117
    declared size 1312 -> distance 357
    declared size 3712 -> distance 3

A larger resource can hold larger distances, so it needs more slots, so the code
over them is wider and every codeword moves. Nothing about the code state is
involved.

This supersedes two earlier notes here, both now withdrawn: that the codeword
assignment changes at a rebuild, and that the slot widths change with it. What
changed in those measurements was the payload, not the state. The slot **bases**
are fixed by the format — one each for distances 1 to 8, then in fours to 44, in
eights to 100, in sixteens to 260, and onward — and are the same in every sweep
here once the alphabet is accounted for.

The rule for anyone measuring this: **state the resource size**, not the literal
count. A field of bits means nothing without it.

One exact reading survives to build on: the specimen's match is 199 bytes — 264
of payload less 65 literals — and its length field begins `0000111100` once its
eight offset bits are taken off.

## Reading codewords out of wimlib directly, which supersedes the sweeps

Every table below replaces one derived by sweeping crafted chunks. The sweeps
were not wrong about the bits; they were wrong about where a codeword ends. Each
one assumed a fixed six-bit codeword, and both alphabets mix two codeword
widths, so every five-bit codeword was read as six bits and the extra bit was
charged to the wrong field. That produced tables that wrapped at `111111` and
resumed at `000000` with the extra-bit count stepping *backwards* — an artefact
of the misreading, not a property of the format. Anything in an earlier section
that shows a wrap is superseded here.

The method that replaces it is cheaper and exact. A payload of *n* literals
followed by one match has a parse wimlib cannot avoid, so its own chunk can be
opened and read: run the range decoder until the first main bit is one — that
counts the literals — and the backward bitstream then sits exactly at the match's
first field bit, because each literal before it spent eight bits. Whatever bits
appear there are wimlib's own codeword for a distance and a length that were
chosen when the payload was designed. One `wimcapture` yields one table row.

The first thing it settled was a check on our own work: encoding 512 literals and
a match of 600 at distance 8 by hand, and asking wimlib to compress the same
payload, produced the same field bits — `011001 00101 10101101`.

## Both alphabets are canonical codes over equal frequencies

A distance or a length is a slot index, coded as a Huffman codeword followed by
that slot's extra bits. Both codes have the same construction and it is the
ordinary one: with *N* slots and `2^k ≤ N < 2^(k+1)`, exactly `2^(k+1) − N`
symbols get *k*-bit codewords and the rest get *k+1*, assigned canonically in
increasing length. The one detail worth stating is which symbols get the short
codes: **the highest-numbered ones**. The nearest distances and shortest lengths
— the common cases — carry the *longer* codeword. That is the tie-break of a tree
built over equal weights, and it is why the tables look rotated: slot 0 is not at
codeword 0.

Two measured sizes fix it. At a resource size of 600 there are 45 offset slots,
so 19 codewords of five bits (values 0..18, held by slots 26..44) and 26 of six
(values 38..63, held by slots 0..25) — slot 0 at `100110`. At 1112 there are 55,
so nine of five bits and 46 of six, and slot 0 sits at `010010`. Both satisfy
Kraft exactly, and both agree with wimlib bit for bit.

## The offset alphabet is sized by the resource, the length alphabet is not

The number of offset slots is the number needed to reach the largest distance a
resource of that size can hold, which is its size less one. At 600 the last slot
covers 581..612 and there are 45; at 1112 the last covers 1061..1124 and there
are 55. The codeword widens with the resource as a result — six bits at 1112,
seven at 6112, eight at 61112, measured by holding a match's length fixed and
watching the length field shift right underneath it.

The length alphabet does not move. The same payload's length field reads
identically at 1112, 6112 and 61112, and the length table taken at 600 matches
the one taken at 1112 row for row. It has 54 slots, always.

    offset slots            length slots
    slot  base   width      slot  base   width
    0..7    1..8     1      0..25   1..26     1
    8..16   9..44    4      26..29  27..34    2
    17..23  45..100  8      30..35  35..58    4
    24..33  101..260 16     36..39  59..90    8
    34..48  261..740 32     40..44  91..170  16
    49..    741..    64     45..46  171..234  32
                            47      235..298  64
                            48      299..426 128
                            49      427..682 256
                            50      683..1194 512
                            51      1195..2218 1024
                            52      2219..4266 2048
                            53      4267..     4096

Both tables were read row by row out of wimlib's own chunks: the offset table by
stepping the distance with the length held fixed, the length table by stepping
the length with the distance held at eight and the resource size pinned so the
offset codeword could not change width underneath the reading.

## The repeat-offset queue starts holding 1, 2 and 3

Stepping the distance from one upwards shows the first three distances behave
unlike the rest: their field bits begin at what is the *length* field for every
other distance. They carry no offset codeword at all. So a match at distance 1, 2
or 3 is coded against the repeat-offset queue, and the queue is seeded with those
three distances before a single item has been read. From distance 4 up the offset
codeword appears and the tables above apply.

## A hand-built LZMS resource that wimlib verifies

All of the above is now enough to write one. A chunk holding 512 literals, then a
match of 600 bytes at distance 8, encoded from these tables and wrapped in the
container described earlier, gives:

    wimlib-imagex verify: successfully verified
    extracted bytes identical to the payload: yes

— hash included, which is the whole claim, since `wimapply` will extract a
resource whose SHA-1 does not match but `verify` will not pass it. Our chunk is
524 bytes where wimlib's is 526.

A shorter test drove the item structure out first: literals, a match, then more
literals, all three decoding exactly as encoded. It had to be kept under 1024
literals in total. Above that the codes rebuild, and a stream that keeps writing
with the old ones decodes into noise — which is what an earlier attempt at this
was actually measuring when it concluded the item stream could not be followed
past the first match. It could; the rebuild had moved underneath it.

## A decoder, and what it reads

The tables above are enough to write a whole-chunk decoder, and one exists in
the scratch harness. It reads a real `wimcapture --compress=LZMS` chunk — range
stream forwards, Huffman stream backwards, literals and explicit LZ matches —
and reproduces the payload exactly. It is not a partial reading of a field; it
is the file back.

Two frontiers stop it, and both are now exact rather than vague.

**The Huffman codes are built by ordinary Huffman over the counts**, with ties
going to the earliest symbol, and codewords assigned canonically in increasing
length and then by symbol index. That construction reproduces all three initial
alphabets — 45, 55 and 54 slots — from nothing but their sizes. Two things were
tested and are wrong: assigning codewords in frequency order breaks the very
first rebuild, and package-merge agrees with plain Huffman here, so
length-limiting is not what distinguishes them.

**Counts start at one and rise by one per symbol coded.** Starting them at zero
breaks the first rebuild, so the floor is real and not an artefact.

**The literal code rebuilds every 1024 literals**, and the first rebuild builds
from the counts accumulated whole. That is measured, not assumed: sweeping the
rebuild point shows a decode that runs to exactly 1024 and no further under any
other choice, and with the rule in place payloads of 1100, 1500 and 2100 bytes
decode end to end, match included.

The second rebuild, at 2048, is where it stops. Its code is nearly the one that
halved counts produce — `max(1, f >> 1)` decodes the first literal past the
rebuild and nothing else does, including `(f+1)>>1`, `(f>>1)+1`, resetting to
one, and halving after the build rather than before. But only that one literal:
the next needs a length vector differing from the halved model in more than one
place, and no single swap of two symbols' lengths recovers it. So the reduction
applied to the counts at a rebuild is close to halving and is not halving. It is
the one rule between here and reading arbitrary LZMS.

## The offset slot widths double at slot 64

The slot table's widths are 1, 4, 8, 16, 32, 64 across 8, 9, 7, 10, 15 and 15
slots — which is 64 slots, ending at distance 1700 — and then 128. The doubling
was found the hard way: a decoder that assumed 64-wide slots continued forever
read every distance correctly up to a resource of 1700 bytes and misread the
first match beyond it. Larger resources will double again, and the boundary each
time is worth measuring rather than assuming.

## Repeat-offset matches, and why they appear so early

A match whose distance is 1, 2 or 3 carries no offset codeword at the very start
of a chunk, which earlier notes recorded without explaining. The explanation is
the repeat-offset queue, and it arrives seeded with exactly those three
distances.

The item is: the main bit, then a range-coded bit choosing an LZ match over a
delta match, then a range-coded bit choosing a repeat over an explicit offset,
then — for a repeat — a **unary index** into the queue, one range-coded bit per
step with its own probability context, and then the length field in the backward
stream as usual. A payload whose first match is at distance 3 codes `0` for LZ,
`1` for repeat, then `1 1 0` selecting entry two, and the backward stream then
begins `010101`, which is precisely the codeword for a length of 2.

Two things follow that matter for anything reading real content. **A repeat match
can be two bytes long** — a single repeated pair of bytes anywhere in a payload
is enough for one, which is why a payload built to be all literals has to avoid
repeated pairs and not merely repeated triples. And a decoder without the queue
does not merely miss an optimisation; it loses its place at the first repeat and
everything after it is noise. That is what stopped an earlier decoder 32 bytes
into ordinary English text.

With the queue in place and entries moving to the front as they are used, a
designed payload that had stopped at its ninth byte runs to its two hundred and
twelfth, where it meets a delta match — the next thing to derive.

## The offset slot widths, measured further out

The widths double as the distances grow, and where they double is not regular
enough to guess. Measured by reading the alphabet size out of wimlib at many
resource sizes — the size fixes the alphabet, the alphabet fixes slot 7's
codeword width and value, and that inverts to the number of slots:

    width   slots      bases
    1       0..7       1..8
    4       8..16      9..41
    8       17..23     45..93
    16      24..33     101..245
    32      34..48     261..709
    64      49..63     741..1637
    128     64..83     1701..4133
    256     84..99     4261..8101
    512     100..      8357..

with further doublings to 1024 and 2048 before a 128 KB chunk's largest
distance, 131071, is reached at around slot 203. The slot counts per region —
8, 9, 7, 10, 15, 15, 20, 16 — are irregular, so the table is worth carrying
verbatim rather than generating. The last three boundaries are known only to
within the sampling step and want a finer measurement before they are trusted.

## Writing is an oracle too

Reading wimlib's chunks answers what it wrote; it does not say whether something
we wrote is acceptable. `wimlib-imagex verify` does, because it checks the
SHA-1: a resource that decodes to anything other than the declared payload fails.
That makes any hypothesis testable by writing a chunk under it rather than
reading one, which is how the rebuild rules above were narrowed — a literals-only
chunk over a small alphabet verifies through two rebuilds under plain
accumulation, and a skewed one does not, which is exactly the discrimination
that reading a single chunk could not provide.

One trap: a chunk that does not come out smaller than its payload is read as
stored, and then verify passes for the wrong reason. Literals-only test payloads
have to be drawn from a small enough alphabet to actually compress.

## The rebuild rule, measured

Every code — literal, offset and length — is rebuilt on its own counter, and the
rule is the same for all three:

    freq_new = (freq_old >> 1) + (times coded since the last rebuild) + 1

with every count starting at one. That form is why the first rebuild looked like
plain accumulation for so long: `freq_old` is 1 there, so `(1 >> 1) + c + 1` is
exactly `c + 1`, and a whole family of wrong rules fits that one data point.
Only the second rebuild separates them.

Finding it needed a different instrument. Reading a chunk says what wimlib wrote;
it does not say whether something we wrote is acceptable. **`wimlib-imagex verify`
does**, because it checks the SHA-1 — a resource that decodes to anything else
fails. So a hypothesis can be tested by writing a chunk under it. Better still, a
chunk of 2048 literals followed by *one* more literal isolates a single codeword:
enumerate the bits for that last literal and the one wimlib accepts is its
codeword, no hypothesis required. Two cautions: a chunk that does not come out
smaller than its payload is read as stored and then verify passes for the wrong
reason; and because the writer pads with zeros, a candidate that is a *prefix* of
the true codeword also passes, so each candidate has to survive both a zero and a
one padding.

Eleven codewords read that way settle the rule exactly, and they also confirm
two things guessing had not: the codes are ordinary Huffman over the counts with
ties to the earliest symbol, assigned canonically by length and then symbol
index; and assigning codewords in frequency order instead breaks the very first
rebuild.

The counters differ per code, measured by finding the exact item that breaks a
decode: **the literal code rebuilds every 1024 literals, the length code every
512 lengths, and the offset code every 1024 offsets.** A payload of literals
alone cannot tell a literal counter from an item counter; one with matches can.

## The last length slot is very wide

The length table ends with two slots of sixteen extra bits each, not the
power-of-two progression the earlier rows suggest. Slot 52 is based at 2219 and
spans to 67754.

This was invisible for a long time because the failure it causes is invisible:
wimlib writes eleven zero bits for that slot when the length is exactly 2219, and
a length that ends a chunk can be read with any number of trailing zeros and
still give the right answer. Only a long match with something *after* it exposes
the width, because only then does a wrong count misalign what follows.

## An encoder, and what it will not write yet

Putting all of it together gives an encoder that wimlib verifies — hash included
— on text of 120 KB, source of 34 KB, runs, and mixed data. It does its own LZ77
parse and owes wimlib nothing about how it factorises; the format is what has to
match.

Three things it learned the hard way. **A short range stream is read past its
end**: the two streams share one buffer and grow toward each other, and the
decoder will read into the other one, so the range stream wants a couple of words
of slack. **An encoder need never use the repeat queue** — coding every offset
explicitly is legal and side-steps the queue's update rules entirely, which is
worth doing because those rules are not settled: an explicit match's distance
does become available as a repeat, but a policy of pushing every one of them
breaks payloads that a policy of pushing none of them survives, so something
about the timing is still wrong. And **matches longer than the last length slot's
base have to be split**.

What it will not write is x86 code. wimlib's decoder applies the format's x86
filter to every chunk unconditionally, so a chunk that decodes to the payload
verbatim is *unfiltered* into something else — which is why binaries fail where
text of the same size passes. The bytes that change are the displacement of a
RIP-relative instruction, which names the filter's job precisely. The filter is
the next thing to derive, and with it the encoder covers everything.

## The x86 filter

wimlib's decoder runs this over every chunk it decompresses, unconditionally.
A chunk that decodes to the payload verbatim is therefore *unfiltered* into
something else, which is why binaries failed where text of the same size passed.
An encoder has to apply the forward transform before compressing.

The transform itself is one line: **a candidate instruction's 32-bit field has
the instruction's own position added to it**, and the decoder subtracts it again.

The interesting part is when it applies. The filter is dormant until it sees the
same *absolute target* — the field plus the instruction's own position — twice.
From then on every candidate is translated, and each translation keeps it alive
for another 1023 bytes; a stretch with no candidates in it lets the filter fall
dormant again. Two instructions sharing an absolute target arm it; two sharing a
displacement do not, which is the check that separates real code from bytes that
merely look like it.

Which instructions count was measured one byte value at a time, each in
isolation inside an armed window:

    e8                          field at +1
    ff 15                       field at +2
    48 8d m / 4c 8d m           field at +3, when m & 7 == 5
    48 8b m                     field at +3, when m is 05 or 0d
    f0 83 05                    field at +3

That last one is a literal three-byte sequence and nothing near it counts: the
same instruction without its lock prefix is not a candidate, nor is the lock
prefix with any other ModRM byte. It appears about once in a hundred kilobytes of
real code, which is enough to put a whole binary out of step.

The lea test is cruder than decoding an instruction — only the low three bits of
the ModRM byte are looked at, so a form carrying an eight-bit displacement, or
none at all, is still read as though it held a 32-bit field. After a candidate
the scan resumes past its field, so an instruction lying inside another's field
is never seen.

Three more details, each worth a binary or two. **`e9` is recognised but never
translated** — the scan steps over its field, so an instruction lying inside it
is invisible, and a filter that does not know this drifts out of step with
wimlib's for the rest of the chunk. **The scan stops seventeen bytes short of the
end** of the buffer, uniformly for every form. And **a call keeps a shorter
window than everything else**: 511 bytes against 1023. That one was the whole
difference between reading half the binaries and all of them, and it makes
sense — an `e8` byte turns up by chance far more often than a REX prefix
followed by `8d`, so the format trusts it for half as long.

The target table is indexed by the low sixteen bits of the target, measured by
giving two instructions targets that differ by exactly 0x10000: they collide and
arm the filter, while a difference of 0x8000 or less does not.

With all of it in front of the encoder, **every x86-64 binary tried is filtered
byte for byte as wimlib filters it** — twenty-five of them, from `ls` to `gcc` —
and twenty-four random streams of candidate instructions agree as well. A WIM of
ten binaries and a text file goes out at 172 KB from 414 KB, verifies, and
extracts identically.

**A remembered target lasts 65535 bytes.** A repeat that far on arms the filter
and one 65536 bytes on does not, which is what a table of sixteen-bit positions
would do. This was the last rule, and it hid behind a broken measurement: the
probe that appeared to contradict it used a buffer of 140000 bytes, which is
larger than a chunk can be, so the offset alphabet was outside the range these
tables were fitted to and the chunk did not decode. Every arming measurement past
131072 bytes was reading noise. Repeated inside a valid chunk size the boundary
is sharp.

With it, arithmetic data filters correctly too — a run of `i * 37 + 11` throws up
an `e8` every couple of hundred bytes by chance, and its only repeats are exactly
65536 apart, so the filter never arms and neither do we.

Measuring any of this needs only one trick: our encoder can write a chunk that
decodes to whatever bytes we choose, so extracting it reads the filter off
directly — whatever comes out is the filter run backwards over what we put in.

## Delta matches

A delta match rebuilds data whose entries step by a constant - a table of
addresses, a run of counters - which is why one turns up thirty-three bytes into
an ordinary binary and about twenty-five hundred bytes into English text. A
decoder that does not know them cannot read real LZMS at all.

The item is the main bit, then the kind bit set (an LZ match clears it), then a
bit choosing an explicit delta from a repeat. An explicit one carries three
fields in the backward stream: a **power**, from a flat code of eight symbols; an
**offset**, from the same slot table the LZ offsets use; and a **length**, from
the ordinary length code. The span is `1 << power` and the reference is
`offset * span` bytes back, so a table of four-byte entries is power two.

Each byte is then

    out[p] = out[p - span] + out[p - reference] - out[p - reference - span]

taken modulo 256 - the same step, applied a span later. It is a per-byte rule
and it does not know about carries, which is worth seeing rather than deducing:
given a table of four-byte values counting up by 0x10, wimlib emits a delta of
exactly 57 bytes, stopping precisely at the entry where the low byte wraps and
the second byte has to increase. It codes that byte as a literal and starts a
fresh delta after it. An implementation that tried to be cleverer than the
format here would produce different output.

The explicit form is written as well as read: the encoder looks for a span and
reference that rebuild a run and emits a delta when it beats the LZ match, and
wimlib and 7-Zip accept the result.

A repeat carries a unary index and a length, and no power or offset. Its
explicit-or-repeat bit has a six-bit history of its own, pinned the same way as
the kind bit - five and seven both fail where six carries every length tried.

**The queue takes an item's reference only after the item that follows it.** So
during an item the queue holds what was in use two items ago, and the head is the
reference before last rather than the last. This is what a table whose reference
alternates shows: with entries 28 and 29 alternating, a decoder that inserts at
once reads every second item from the wrong entry, and parts at the first carry -
which is the only byte where a wrong reference differs from the right one.
Inserting a step late reads every one of them correctly, in four payloads.

Each entry carries its own span; the earlier reading, that the span comes from
the newest delta, was measured against the immediate queue and does not survive
the delayed one.

The same delay is applied to the LZ offsets, on the same evidence - it never
reads worse and reads several payloads further.

**The queue's seed is one reference, power zero and offset one.** A chunk whose
first delta is a repeat naming index zero was written and wimlib verifies it, so
the seed is not a guess.

**Every one of the four decisions around a repeat carries a six-bit history**, and
this project had three of them as single probabilities. The explicit-or-repeat bit
of an LZ match, and each bit of the unary index on both sides, are contexted
exactly as the delta explicit-or-repeat bit is.

The measurement that settles all of them is the same, and it is worth stating as a
method: **run the test over a payload where every candidate gives the same bytes.**
A run of one byte is reproduced by a match at any distance and by a delta of any
span and reference, so writing a growing run of repeats over it tests nothing but
how their bits are coded. The signature is then unmistakable - a history of n bits
carries n+1 repeats - and six carries every length tried, up to thirty, where
seven and wider fail at twelve.

The first attempt at the delta index used a progression instead, and it said no
width worked at all: a queue entry seeded with power zero cannot rebuild a
progression, so the payload was failing for a reason that had nothing to do with
the coding. A payload that discriminates too much is as useless as one that
discriminates too little.

**The format refuses a match longer than 67754**, which is the last length the slot
below the top names; the top slot is never usable. Measured by writing single
matches over a repeating phrase with **more items after them**, which is the part
that matters: with the match last, leftover bits in the backward stream are never
read and any length appears to pass. That first reading said 67755, and it was an
artefact of testing the last item in a chunk.

**The last length symbol is not a length: a match carrying it runs to the end of the
chunk.** It reads as 67755 plus sixteen extra bits, and that reading survives every
payload whose matches are shorter, because nothing else uses the symbol. Three things
say what it means, and only together: writing it is refused whenever an item follows
it and accepted when it is the last, which is what a run-to-the-end match would do;
wimlib's chunk for a long repeating text carries it and decodes byte-exact under this
reading and no other; and it makes that chunk two bytes smaller than the two explicit
matches it would otherwise need, which is exactly the gap that could not be accounted
for. Every one of the eight delta-heavy payloads wimlib writes for the original
probe set now reads back byte-exact.

The writer declines it. Emitting it for the matches this parse makes is refused, and
what a reader needs is not what a writer may use.

**Our offset table agrees with wimlib at every distance tried**, from one to
twenty-four thousand, measured the same way - one match at that distance with items
after it.

**Our length code agrees with wimlib symbol for symbol.** Writing one match at the
first length of each of the fifty-four slots verifies for every one the format
takes, and fails only for the top slot it never takes. So where a resource is
misread it is not the length alphabet.

**Our chunks are wimlib's chunks, byte for byte.** Take a chunk wimlib wrote, decode
it to its items, and write those items back: the result is identical to what wimlib
produced, for every payload the reader gets right. That is a sharper instrument than
verification, because it turns "acceptable" into "the same", and it found the last
thing that was not: the range coder's flush emits **one** word of slack after its two
shift-outs, where this project emitted two. Two extra zero bytes at offset eight were
the only difference between our encoding and theirs.

One warning about using it. Widening the last-but-one length slot from sixteen extra
bits to seventeen makes a payload that used to fail read back exactly - and breaks
every written length above 2218. It is not a fix but a coincidence: the extra bit
happens to re-align a stream that was already off by one, and reading alone cannot
tell the two apart. Any change that improves reading has to be put to the writer
before it is believed.

It is also the instrument to reach for next. With the encoder identical, the item
stream that produced any chunk can be searched for rather than guessed: encode a
candidate stream and compare bytes. The two payloads still misread are 60-byte chunks
that no stream reconstructed here reproduces - the closest is 62 bytes - so their
parse holds something not yet built.

**The delayed queue is confirmed by writing on the LZ side.** A repeat straight
after an explicit match is rejected; the same repeat with one literal between it
and the match verifies, and with two literals it still verifies. The distance
reaches the queue after the item that follows it, exactly as reading said.

Every one of the eight delta-heavy payloads wimlib writes for the original probe
set now reads back byte-exact, against three when this began.

**Naming a recent offset spends it.** The entry is taken, the ones above it move
down, and the seed that has not been used yet takes the last place - so a queue
seeded 1, 2, 3 becomes 1, 2, 4 once index two has been named. Nothing else explains
a chunk in which two consecutive repeats both name index two and the second has to
mean four, and both instruments agree: under this rule every delta-heavy payload
wimlib writes here decodes byte-exact, and a chunk written to need it verifies.

Finding it needed the range-coded half of the mirror. Writing the decoded items back
and comparing from the chunk's **end** checks the Huffman fields; comparing from its
**start** checks the arithmetic-coded decisions, and only the second could say which
index a repeat named. A reading confirmed by one half and not the other is not
confirmed.

What is left is not in that set at all. Tables whose entry width and step change
from block to block - so the reference a delta names alternates - still stop part
way: three of four such payloads built for this fail, where all eight of the
original probe set now pass. Whatever governs which recent *delta* a repeat names
is the same question this queue answered for LZ offsets, and the same rule does not
answer it: applying consumption to the delta queue changes nothing either way.

The rest of the ground is covered:The rest of the ground is covered: not the length alphabet, whose fifty-four
symbols are each confirmed by writing; not the offset alphabet, whose size was
swept twelve either way; not the main or kind state width, swept two to eight; not
the LZ explicit bit's width, swept nought to eight; not a queue that collapses
duplicates, which a written chunk refutes; and not the x86 filter, which moves no
byte of any payload here and round-trips every one.

**The queue keeps a reference it already holds** rather than collapsing it. A chunk
was written with two matches of the same distance and then a repeat naming index
one, which under a collapsing queue would name a seed and rebuild the wrong bytes;
wimlib verifies it. Worth stating because collapsing carries one payload 170 bytes
further when reading, which is exactly the kind of gain that is not evidence.

And what the queue is *not*: an LZ match does not feed it. A chunk was written
whose only delta is a repeat, preceded by an LZ match of distance eight over a
table of eight-byte entries, so a queue fed by that match would rebuild the table
and a queue holding only the seed would not. wimlib rejects it, which settles the
reading that a repeat's reference is the LZ match's distance factored into a span
and an offset - a reading that carried exactly one item further.

## What is not derived

- which recent delta a repeat names when the references alternate. This is the
  last thing between here and reading every LZMS resource wimlib writes, and the
  only thing on this list that costs correctness.
- the last of the x86 filter's candidate table. A few instruction forms are still
  read differently by the two scans, which costs a handful of bytes per binary.
  It costs no correctness on anything measured here: the filter moves no byte of
  any probe payload, including one with 798 of the byte it keys on, and every one
  round-trips.
- the offset slot widths past twenty-four thousand, which only large resources
  reach. Every distance below that is confirmed by writing.

An encoder needs none of wimlib's parsing choices — any valid factorisation will
do — so it may decline repeats, deltas and the filter entirely and still produce
a resource every reader accepts. The decoder has no such freedom and needs all of
it.
