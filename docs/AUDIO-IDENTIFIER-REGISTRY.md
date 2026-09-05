# Audio codec identifiers

The support ledger for the audio domain — which codecs and containers exist and what each can
do — lives in
[`Hawkynt.FileFormats.Audio/README.md`](../Hawkynt.FileFormats.Audio/README.md), including the
historical WAVE/ACM tags each codec answers to and the families deliberately left out of scope.
This page is the part that is not a table: where that identifier catalogue comes from, why it is
not a to-do list, and what a unified identifier registry would have to carry.

## Where the identifiers come from

- The [IANA WAVE/AVI codec registry](https://www.iana.org/assignments/wave-avi-codec-registry/wave-avi-codec-registry.xhtml)
  and [RFC 2361](https://www.rfc-editor.org/rfc/rfc2361) are the primary registration sources.
- [GSpot v2.70a audio codecs](https://gspot.headbands.com/audiocodecs.htm) and its
  [mirror](https://www.headbands.com/gspot/audiocodecs.htm) are a historical alias catalogue of
  245 tags, useful for what encoders in the wild actually stamped into headers.
- Microsoft's WAVE documentation and the format-specific standards define framing and
  extra-data semantics.

The order matters, because GSpot is an identification oracle and not a specification. Two
mappings taken from it on trust turned out to be wrong: `0x0039` is Roland RDAC in the IANA
registry rather than an IMA/DVI alias, and `0x0130`-`0x0135` is not one SIPR bucket —
`0x0133` and `0x0134` are G.729 and G.729A registrations and `0x0135` is Kelvin.

Independent implementations — FFmpeg, libgsm, spandsp, the ITU-T G.191 tools — are used as
behavioural oracles. Where a licence does not permit adapting the code, only its observable
bitstream behaviour is used, the implementation is written independently, and the result is
pinned with the repository's own tests.

## 245 tags are not 245 codecs

The catalogue must not turn into 245 decoder projects. Most entries are aliases, vendor
registrations, transport wrappers, or several tags for one codec family. A family is implemented
once and every compatible tag maps onto it. That is why the README's codec table has an
identifier column rather than a row per tag: the tag is an identity question, and only the family
behind it is an implementation question.

The corollary is that "unimplemented tag" and "unimplemented codec" are different states, and the
README distinguishes them. A tag that names a family we already decode but is not yet wired to
the WAVE reader is a routing gap. A tag naming a codec nobody here has written is a codec gap.
Only the second needs a decoder.

## Architectural target: one registry, many identifiers

The tag-to-family mapping is currently spread across the readers that need it. A single audio
identifier registry should instead carry, per codec family:

- the canonical codec family;
- WAVE/ACM numeric tags;
- RIFF/AVI FourCC aliases where applicable;
- ISO-BMFF sample entries and object types;
- Matroska codec IDs;
- Ogg mappings;
- RealMedia identifiers;
- profile and variant metadata;
- decoder/encoder availability, independently of whether an identifier is recognised.

That last point is the one worth keeping separate: recognising a tag and being able to decode
what it labels are different capabilities, and collapsing them is how a support table starts
lying. With the mapping as data, the historical-tag audit becomes an interoperability problem
with table-driven tests — each proving that a tag resolves to the expected family *and* that the
container framing for that tag is valid — instead of a pile of unrelated implementations.

## Cross-conversion coverage

`Compression.Lib.AudioConversionInventory` reports the conversion graph from the registry
rather than from this page, so the numbers below are measured and go stale the moment the
code changes. At the time of writing: 118 audio formats, every one usable as a source, and 59
usable as a target.

A target is reached by one of four routes, tried least-destructive first — byte-exact
passthrough, packet remux, PCM encode, and building the container from per-channel WAVs. Only
14 targets carry a real PCM encoder; the rest are built from channels, which is why the
channel bridge is not a fallback so much as the main road.

Two properties are worth stating because they were not true until they were fixed. A source
that lists no `Channel` entries — every mono file, since one channel has nothing to split — is
decoded and split by the pipeline instead of being refused. And a target that rejects the
sample width it is handed is offered another, after refusing, rather than ending the
conversion; widening is exact, and narrowing only happens where the target accepts nothing
wider.

Measured over 51 files the graph built itself, converted into eight representative targets,
397 of 400 pairs succeed. The three failures are MP3 asked for sample rates outside 8-48 kHz.
Closing those means resampling, which nothing here does yet — and a bad resampler is worse
than none, so it is named here rather than approximated.

### What a foreign stream proves that a round trip cannot

Every codec here was checked by encoding with it and decoding with it again, which proves only
that the pair agrees with itself. Decoding a stream *libavcodec* produced, and having
libavcodec or the format's own reference tool decode ours, is what makes two implementations
disagree out loud. Measured that way, against ffmpeg 9.0.1 and the reference `wvunpack`:

- **FLAC**, **WavPack** and **QOA** decode a foreign stream bit-exactly, and a foreign decoder
  reads ours back losslessly — for WavPack, `wvunpack -v` verifies the file outright.
- **Opus** matches libopus sample for sample within 2 LSB and now ends on the exact sample,
  once the final page's granule position is honoured rather than only the pre-skip.
- **TTA** throws part-way through a frame whose CRC it has just checked, and ffmpeg refuses
  ours in turn.
- **AC-3** and **DTS** return no samples at all from a foreign stream, though ffmpeg decodes
  what they write.

The last three are the same failure in three places: a decoder that reads what our own encoder
writes and gives up on the first frame of anyone else's. TTA is unrouted, and AC-3 and DTS now
refuse rather than hand the pipeline nothing — it will build a valid, entirely empty file out
of no samples, and an empty file that claims to be a conversion cannot be told apart from
silence that was really in the source.

Conversions that remain refusals by nature, not gaps: DSDIFF and DSF take per-channel raw DSD,
so PCM must first be sigma-delta modulated; MIDI takes MIDI tracks, and turning audio into
notes is transcription; and the chiptune and tracker families decode only, because "encoding"
arbitrary audio into register writes or pattern data is the same problem.
