# FLV mux/remux notes

This branch adds a managed FLV mux/remux path based on Adobe's FLV v10.1 container definition.

Implemented scope:

- low-level FLV tag writing with 24-bit DataSize, split 32-bit timestamps, StreamID and PreviousTagSize
- native-tag remux preserving tag payloads, timestamps and ordering while rebuilding structural sizes
- AAC and MP3 audio-only muxing through the repository's encoded-audio packet abstraction
- AAC packet demux for packet-preserving cross-container remux
- bounds checks for tag sizes, offsets, back-pointers and timestamps

Behavioral reference: FFmpeg's LGPL FLV muxer was used only as an oracle for interoperability details; no implementation code was copied or closely translated.

Normative/reference material:

- Adobe Flash Video File Format Specification v10.1, Annex E
- Adobe AMF0 File Format Specification
- ISO/IEC 14496-3 AudioSpecificConfig / ADTS
