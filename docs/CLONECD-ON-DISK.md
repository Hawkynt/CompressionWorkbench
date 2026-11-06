# CloneCD image sets (what our writer produces)

A CloneCD image is three files that share a base name:

- `.ccd`, an INI-style control file that carries the TOC;
- `.img`, the raw 2352-byte sectors from LBA 0;
- `.sub`, 96 bytes of subchannel per sector.

Our writer produces a single-session disc with one Mode 1 data track, from an ISO image. Each rule
below was checked against third-party tools and real dumps; `CloneCdWriteExternalConformanceTests`
pins it.

## `.img`: raw Mode 1 sectors (ECMA-130)

Each sector holds:

- 12 sync bytes;
- the BCD address (LBA + 150 frames, so LBA 0 is 00:02:00) and mode 1;
- 2048 bytes of user data;
- the EDC over bytes 0–2063;
- 8 zero bytes;
- the P and Q parity (ECC) over the header and data.

PowerISO's conversion of the same ISO to MODE1/2352 sectors is byte-identical. Aaru reports every
sector checksum correct.

## `.sub`: 96 bytes per sector

The 96 bytes hold the P channel (12 bytes), then the Q channel (12 bytes), then R to W (72 bytes,
zero).

- **P** marks pauses. On the measured pressed discs, P is set on the first sector after the pregap
  (LBA 0) and on the last 149 sectors before the lead-out. Everywhere else it is clear.
- **Q**, mode 1 (position), holds 12 bytes:
  - control/ADR `0x41` (data track, ADR 1);
  - track `01` and index `01`;
  - the BCD time within the track;
  - a zero byte;
  - the BCD absolute time;
  - CRC-16/CCITT (polynomial `0x1021`, initial value 0) of the first 10 bytes, inverted and stored
    big-endian.

  The CRC formula reproduces Q frames measured from two independent sources: Aaru's generator and a
  real CloneCD dump.

The real dump is "PSX - Fighting Force 3-24" on archive.org, a single data track. Our generator
reproduces its P and Q channels for every sector the drive read cleanly.

## `.ccd`

The file has CRLF line endings and contains these sections:

- `[CloneCD]` with `Version=3`;
- `[Disc]` with `TocEntries=4`, `Sessions=1`, `DataTracksScrambled=0` and `CDTextLength=0`;
- `[Session 1]` with `PreGapMode=1` and `PreGapSubC=0`;
- four TOC entries, each with `Session=1`, `ADR=0x01`, `Control=0x04`, `TrackNo=0`, `AMin`/`ASec`/
  `AFrame` 0, `ALBA=-150`, `Zero=0`, and `PMin`/`PSec`/`PFrame` with `PLBA = MSF − 150`:

  | Entry | Point | PMin / PSec / PFrame |
  | --- | --- | --- |
  | A0 | first track | 1 / 0 (CD-ROM) / 0 |
  | A1 | last track | 1 / 0 / 0 |
  | A2 | lead-out | the MSF after the last sector |
  | 1 | track 1 | 00:02:00 |

- `[TRACK 1]` with `MODE=1` and `INDEX 1=0`.

This matches the layout of real CloneCD control files. Aaru 5.4.2 reads our set as "CloneCD
version 3": one session, one CdMode1 track with a raw subchannel, and the lead-out at the expected
MSF. Aaru also extracts the ISO 9660 files through our set unchanged, and ccd2iso turns our `.img`
into the input ISO.
