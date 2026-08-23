# Quantum — on-disk notes

Quantum is one of the three codecs a cabinet may hold. Microsoft never published
a specification for it: it was licensed from David Stafford's Quantum archiver
and, unlike LZX, no format document accompanied it.

What is written here was derived by asking `cabextract` — which is libmspack, the
reference reader — what it makes of cabinets we build. No implementation's source
was read; `AGENTS.md` forbids it. The structural scaffolding came from published
descriptions of the format, cited at the end, and every constant below was then
measured rather than taken on trust.

## Why this is harder than LZMS

LZMS fell quickly because `wimcapture` **writes** it: correct streams could be
read and compared against. Nothing on hand writes Quantum — 7-Zip and cabextract
only read it, and a scan of this machine turned up no Quantum cabinet. That
leaves two instruments, both built on the same trick of putting a bitstream of
our choosing into a cabinet and extracting it: read what the decoder makes of an
arbitrary stream, and ask whether it accepts one we meant. Neither ever shows a
correct stream to copy.

## The folder

A cabinet folder names its codec in `typeCompress`. For Quantum that is

    2 | (level << 4) | (window_bits << 8)

with window bits from 10 to 21. Our writer used a scale of its own, which is why
libmspack answered "out of memory": it was reading a window of two bytes.

## The models

Nine adaptive models, each a list of symbols with frequencies:

    selector      7 symbols
    literal       4 models of 64 symbols: 0..63, 64..127, 128..191, 192..255
    position      3 models, one each for selectors 4, 5 and 6
    length        27 symbols

The selector says what comes next: 0 to 3 pick a literal model, and the byte it
yields is emitted directly. 4, 5 and 6 are matches of three, four, and five or
more bytes; only 6 carries a length.

**Frequencies start at one and rise by eight**, measured by sweeping the
increment against cabextract's output — 8 is a sharp peak, not a plateau.

**The symbols are not kept in frequency order — except at a sorting rescale.**
Published descriptions say Quantum sorts them, and an early reading here did so on
every update: it decodes nothing at all. But a model *is* sorted, by descending
count, at its **fourth rescale and every fiftieth after that** — read directly out
of libmspack, which holds the order the fourth left behind through rescales 5 to
53 and changes it at 54. The count is per model, so a payload that drives one
literal model sorts it on its own crossing rather than the selector's.

**That rescale halves differently from the others**: each count on its own,
rounded up, rather than the cumulative array of the ordinary case. Three
independently measured selector tables pin it — 1074, 953, 936, 841 becomes 537,
477, 468, 421 — and no rounding of the cumulative reproduces any of them.

**Equal counts are not left where they were.** Which order they end up in decides
what every later symbol decodes to, and no rule stated as a comparison reproduces
it: the same tie keeps its order in one table and reverses in another. What does
reproduce it, exactly, is a **bubble sort that counts equal neighbours as out of
order** — carrying the larger counts towards the end and reading the table back to
front. That was fitted to three sixty-four symbol tables read out of libmspack,
192 positions with none wrong, having tried some two hundred arrangements of
selection, insertion, shell, heap, comb, merge and quicksort, whose best managed
39 of 64.

**A model rescales on its own account when its total passes 3800.** Which model
crosses first is visible in where a decode goes wrong: spread a payload over all
four literal models and the seven-symbol selector model crosses first, at item
475; confine it to one literal model and that model crosses first, at item 468.
What a rescale does has a section of its own below.

## The coder

An arithmetic coder over sixteen-bit `low`, `high` and `code`, in the shape the
CACM87 paper describes: `code` starts as the first sixteen bits, a symbol is
found by `((code - low + 1) * total - 1) / (high - low + 1)` against cumulative
frequencies running downwards, and renormalisation shifts while the top bits
agree, with the usual underflow case when `low` and `high` straddle the middle.
The symbol has to be read from the model *before* the model updates itself.

## Slots

Position slots are the doubling table: 42 slots, extra bits 0, 0, 0, 0, then two
slots each of 1, 2, 3 … up to 19, with bases running 0, 1, 2, 3, 4, 6, 8, 12, 16,
24 and so on. A distance is `base + extra + 1`.

How many slots a model holds depends on the window — only those whose base is
below it, so a 1 KB window gives twenty — **and on which selector it serves**:

    selector 4 (matches of three)        at most 24 slots
    selector 5 (matches of four)         at most 36 slots
    selector 6 (matches of five or more) the whole table, up to 42

Measured by sweeping each model's size against cabextract for a match it has to
decode: at a 2 MB window the three answers are 24, 36 and 42, at 256 KB they are
24, 36 and 36, and at 8 KB they are 24, 26 and 26. This is what the published
remark that the first two selectors have a smaller maximum slot amounts to. It
matters more than it sounds: a model one symbol wider than the decoder's has a
different alphabet, and then nothing decodes at all.

Length slots are 27, and their shape was measured by sweeping: **six slots
carrying no extra bits, then four slots at each of 1, 2, 3, 4 and 5 extra bits**.
A length is `5 + base + extra`.

## The window starts as zeros

A match may reach back past the start of the data, and what it finds there is
zeros. This is not a detail one would guess: a decoder that refuses such a match
stops on perfectly ordinary streams, and it was the difference between three of
five test streams decoding and all five.

## Writing, which is the instrument that matters

Reading cabextract's decode of a chosen bitstream says what the decoder does with
that stream. It never says whether a stream *we* built is one it accepts. Writing
an encoder and handing cabextract the result does, and it is worth the trouble:
the answer is a byte-exact yes or no, and the first item that differs points at
the mechanism.

Against that instrument, text, source, runs, random bytes, a single byte and all
zeros all round-trip **exactly** — every byte, every item kind, matches and
literals alike. Each time the round trip stopped somewhere, the place it stopped
named the mechanism that was still wrong: the first rescale, then the fourth,
then the fifty-fourth, then the end of the first data block.

## Where the extra bits go

Position and length slots wider than zero bits carry their extra bits raw, not
through the arithmetic coder — carrying them as one equiprobable symbol of 2^n,
or as n equiprobable bits, breaks streams that otherwise decode. But raw *where*
is the whole question, and getting it wrong looks like getting the distance
wrong: a slot meaning 5 or 6 came back as 8.

The decoder reads them at the point its own reading has reached, which is sixteen
bits — the coder's priming — ahead of what the coder has emitted, because those
sixteen bits were consumed before the first symbol was ever decoded. So an
encoder cannot simply append them: they belong sixteen bits further on, in the
middle of coder output it has not produced yet. Writing them at the current
position works only by luck, and which payloads are lucky changes with the data,
which is what made this look like a rounding fault rather than a placement one.

One corollary worth keeping: the gap has to be *made* when the stream is still
shorter than it — early on the coder has emitted fewer bits than the decoder has
already read — or short streams put their extra bits in the wrong place.

## Reading their table directly

A sharper probe than round-tripping: encode the items both sides agree on, then
steer the coder into a chosen thin slice of its range instead of coding a symbol
from a model, and declare the block one byte longer than the agreed prefix. The
byte cabextract emits for that last position names the symbol its own table
assigns to that slice. Sweeping the slice walks the boundaries of its cumulative
table, which is its frequencies, to about one part in sixteen thousand.

Calibrating on a prefix whose table is known confirms the probe reads it back
unchanged. This is what settled the sorting rescale: three tables read out
symbol by symbol, and an arrangement fitted to all three at once rather than to
the ties of any one of them.

A selector table can be read the same way even though its symbols are not bytes:
where the slice lands on a literal selector, the byte that comes out lies in that
selector's group of sixty-four, so its top two bits name the selector. That is how
the second sorting rescale was found — libmspack's selector order stayed put
through rescales 5 to 53 and changed at 54.

Three cautions, all learned the hard way: above the last literal's boundary the
emitted byte is a *copied* one, so it cannot be read as a selector; a payload
built by constructing a fresh seeded generator per byte is a constant, not a
sample; and the probe must re-use one parse of the prefix rather than re-parsing
it for every slice, or a sweep takes hours.

## The rescale

A model rescales on its own account when its total passes 3800 — per model, not
shared, since rescaling every model together is measurably worse. What it does is
not a halving of the frequencies. It is a halving of the **cumulative** array,
rounded down, followed by forcing that array back to strictly decreasing so that
no symbol is left uncodeable:

    h[i] = cum[i] >> 1                       for every i, with h[n] = 0
    for i from n-1 down to 0:                 every symbol keeps at least one count
        if h[i] <= h[i+1]: h[i] = h[i+1] + 1
    freq[i] = h[i] - h[i+1]

The second step is what made this hard to see. Halving frequencies individually
and halving the cumulative array differ only in rounding, and either can be
argued from a single measurement; the repair pass is invisible in a model whose
counts are all large and decisive in one whose counts are mostly one. Two
measured tables pin it exactly and jointly: counts of 817, 1017, 945, 1025 and
three ones become 408, 509, 472, 511 and three ones, while 3801 and six ones
become 1897 and six ones — the three counts the repair invents are taken out of
the largest symbol, because the halved total is what the cumulative fixes.

Reading confirms it independently: **sixteen of seventeen** streams cabextract
decoded from random input now decode identically here, where halving the
frequencies gave ten.

## Writing it, and what a folder can hold

`CabWriter` writes Quantum, and `cabextract` — libmspack — tests and extracts what
it writes, byte for byte, for text, source, runs, zeros, single bytes, data that
resists compression, and eighty thousand bytes of it. That is checked by a test
rather than asserted here.

**A folder is a run of data blocks, and its models carry across them** while its
coder starts afresh in each. A block that restarts the models decodes as noise
from its first byte; one that carries them reads. A match may reach back over the
whole folder, window permitting, but not past the end of its own block, since a
block says how many bytes it decodes to.

That was the last limit. Until the sorting rescales were measured a folder had to
close before the first of them, which for data that resists compression is 1188
bytes, and a file larger than one folder was refused.

## What is still missing

Nothing measured is missing. The one thing not exercised is a window other than
the fifteen bits a cabinet usually names, and folders whose data blocks are
shorter than the 32 KB this writer emits.

## Sources

The structure above — nine models, the selector's meanings, 42 position slots
with 0 to 19 extra bits, 27 length slots with 0 to 5, and windows of 2^10 to
2^21 — comes from published descriptions of the format:

- Matthew Russotto, "Quantum compression format", <http://www.russotto.net/quantumcomp.html>
- <https://github.com/dcarrero/unquantum>, whose notes summarise the same structure

Everything else here was measured against cabextract.
