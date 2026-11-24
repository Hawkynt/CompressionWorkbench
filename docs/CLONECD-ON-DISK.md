# CloneCD image sets

A CloneCD image is three files that share a base name:

- `.ccd`, an INI-style control file that carries the TOC;
- `.img`, the raw 2352-byte sectors;
- `.sub`, 96 bytes of subchannel per sector, parallel to the `.img`.

Our writer produces discs with one or more sessions, each holding audio (CD-DA), Mode 1 and Mode 2
(CD-ROM XA, Form 1 and Form 2) tracks. Our reader lists every track of such a set. Each rule below
says how it was checked; `CloneCdWriteExternalConformanceTests` and
`CloneCdMultiTrackExternalTests` pin the checks.

## `.img`: raw sectors (ECMA-130)

- **Audio**: 2352 bytes of 16-bit stereo PCM, little-endian.
- **Mode 1**: 12 sync bytes, BCD address (LBA + 150), mode 1, 2048 bytes of user data, EDC over bytes
  0–2063, 8 zero bytes, then P and Q parity (ECC) over the header and data.
- **Mode 2 Form 1**: sync, address, mode 2, the 8-byte subheader (two equal copies), 2048 bytes of
  data, EDC over subheader and data, and ECC computed with the address zeroed.
- **Mode 2 Form 2** (subheader byte 2, bit 5): sync, address, mode 2, subheader, 2324 bytes of data,
  then the EDC over the subheader and data in the last four bytes. Form 2 has no ECC.

Evidence:

- PowerISO's MODE1/2352 conversion of an ISO equals our Mode 1 track byte for byte.
- Every Mode 2 sector of a Video CD from vcdimager 2.0.1 (Form 1 and Form 2, from an MPEG-1 clip
  encoded by ffmpeg) is rebuilt byte for byte from its subheader and data.
- Aaru reports every sector checksum correct on our sets.

### Where the sectors sit

The `.img` holds each session from its first track's INDEX 01 to its lead-out. The sessions follow
one another. Lead-outs, lead-ins and the pre-gap before the first track of each session are not
stored.

Within a session, every sector between two TOC points belongs to the earlier track in the `.img`,
including the next track's pre-gap. This is how Aaru 5.4.2's reader maps sets: given a
two-session set in three candidate layouts, it read the second session at the right place only in
this one. Aaru's own CloneCD writer stores a single session and refuses discs with more. We have no
real multi-session CloneCD dump, so the multi-session layout is checked against Aaru's reader only.

### Layout our writer uses (ECMA-130 clause 20, Orange Book session gaps)

- Track 1 of the first session starts at LBA 0 after an unstored 150-sector pause.
- A data track followed by an audio track ends with a 150-sector post-gap: sectors of its own mode
  with zero user data. The audio track then starts with a 150-sector pause of silence (INDEX 00).
- A data track after an audio track, or after a data track of the other mode, starts with a
  225-sector pre-gap. The first 75 sectors are coded as the preceding track: same Q control field,
  same sector mode. The remaining 150 are coded as the new track.
- Tracks of the same kind follow each other without a gap.
- Session 2 starts 6750 sectors (90 s lead-out) plus 4500 (60 s lead-in) after session 1's lead-out.
  Later sessions start 2250 (30 s lead-out) plus 4500 sectors after the previous lead-out. Each
  session's first track has an unstored 150-sector pre-gap.
- Audio that is not a whole number of sectors is padded with silence.

## `.sub`: 96 bytes per sector

The 96 bytes hold the P channel (12 bytes), the Q channel (12 bytes) and R to W (72 bytes, zero).

- **P** (ECMA-130 22.2) carries flags. A flag is at least 150 sections long, or as long as the pause
  when the pause is longer. Its last ONE is the first section with user data. Our writer sets a flag
  ending on every track's INDEX 01 and on every session's lead-out start (the lead-out itself is not
  stored).
- **Q**, mode 1 (position), holds 12 bytes:
  - the control nibble (`0` audio, `4` data) with ADR 1;
  - the track number and index (`00` in a pause, `01` after it);
  - the BCD time in the track. In a pause it counts down and is zero in the last pause section
    (ECMA-130 22.3.3.3). From INDEX 01 it counts up from zero.
  - a zero byte;
  - the BCD absolute time (LBA + 150);
  - CRC-16/CCITT (polynomial `0x1021`, initial value 0) of the first 10 bytes, inverted and stored
    big-endian.

  In the first 75 sectors of a transition pre-gap, the control nibble is the preceding track's.

Evidence and limits:

- **Single data track**: the real dump "PSX - Fighting Force 3-24" (archive.org) matches our P and
  Q channels in every sector the drive read cleanly.
- **Audio and multi-session**: no real dump was available. These follow ECMA-130 clause 22 as
  written.
- **Aaru's subchannel generator** (`--generate-subchannels`) is not used as an oracle. On a
  mixed-mode cue it marks track 1's first 150 sectors as a pause and labels track 2's pause
  INDEX 01. Both contradict the standard.

## `.ccd`

The file has CRLF line endings and contains these sections:

- `[CloneCD]` with `Version=3`;
- `[Disc]` with `TocEntries`, `Sessions`, `DataTracksScrambled=0` and `CDTextLength=0`;
- `[Session n]` with `PreGapMode` (the first track's mode) and `PreGapSubC=0`;
- per session, these TOC entries, each with `Session`, `Point`, `ADR`, `Control`, `TrackNo=0`,
  `AMin`/`ASec`/`AFrame`, `ALBA`, `Zero`, and `PMin`/`PSec`/`PFrame` with
  `PLBA = MSF − 150`. Values are decimal.

  | Entry | ADR | Values |
  | --- | --- | --- |
  | A0 | 1 | `PMin` is the first track, `PSec` is the disc type (0 = CD-DA/CD-ROM, 32 = CD-ROM XA when any Mode 2 track exists) |
  | A1 | 1 | `PMin` is the last track |
  | A2 | 1 | the lead-out MSF |
  | 1–99 | 1 | the track's INDEX 01 MSF |
  | B0 | 5 | every session but the last: `AMin`/`ASec`/`AFrame`/`ALBA` give the next program area's start, `Zero=1`, and `PMin`/`PSec`/`PFrame` give 79:59:74 |

  The B0 values follow the Orange Book description of the pointer. No real multi-session control
  file was available to compare them with.

- `[TRACK n]` with `MODE` (0 audio, 1, 2), `INDEX 0` when the track's pause is stored in the `.img`,
  and `INDEX 1`.

The single-session data-disc layout matches real CloneCD control files. Aaru 5.4.2 reads our sets as
"CloneCD version 3", including the sessions, the track types and the sector placement. ccd2iso
turns a Mode 1 `.img` back into the input ISO, and bchunk splits a mixed-mode `.img` into the ISO
and the WAV we put in.

## Reading

The reader maps tracks from the TOC entries alone. It accepts the `[TRACK n]` sections of
`Version=3` files and works without them: Aaru writes `Version=2` files that have none. Without
`MODE`, a track is audio when its control field has no data bit. Otherwise the sector mode byte of
its first sector decides between Mode 1 and Mode 2.

The tracks are listed as `track-NN.iso` (Mode 1 user data), `track-NN.wav` (CD-DA in a RIFF WAVE)
and `track-NN.mode2` (2336 bytes per sector: subheader and data). Tracks of later sessions sit under
`session-NN/`. The same names fed to the writer make the same disc.

A data track's extent stops at the next track's INDEX 00 when the control file names it. Otherwise
trailing sectors of another mode are left out: an audio pause that Aaru's files do not mark. A sector
of the wrong mode inside the track is refused rather than returned as data. The reader shows no
tracks when the data tracks are scrambled (`DataTracksScrambled=1`), the TOC lacks a lead-out, or
the `.img` is shorter than the TOC describes.

Aaru-converted sets read back exactly:

- a Video CD from vcdimager, whose tracks are vcdimager's Mode 2 sectors;
- a mixed-mode cue of PowerISO's Mode 1 sectors and audio, which gives back the ISO and the PCM.
