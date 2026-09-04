# Audio codec identifiers

The support ledger for the audio domain — which codecs and containers exist and what each can
do — lives in
[`Hawkynt.FileFormats.Audio/README.md`](../Hawkynt.FileFormats.Audio/README.md), including the
historical WAVE/ACM tags each codec answers to and the families deliberately left out of scope.
This page is the part that is not a table: where that identifier catalogue comes from, why it is
not a to-do list, and what a unified identifier registry would have to carry.

## Where the identifiers come from

- [GSpot v2.70a audio codecs](https://gspot.headbands.com/audiocodecs.htm) — 245 historical WAVE/ACM format tags and aliases.
- [GSpot mirror](https://www.headbands.com/gspot/audiocodecs.htm) — the same catalogue where the subdomain is unavailable.

GSpot is an alias and identification oracle, nothing more. Standards-body specifications and
registered format documentation govern implementation behaviour; where the two disagree, the
specification wins and GSpot is treated as a record of what encoders in the wild once stamped
into a header.

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
