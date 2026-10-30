# HxC Stream HFE on disk

The HxC "Stream HFE" image (PRONOM fmt/2039, signature `HxC_Stream_Image`) is a flux image format.
It shares the `.hfe` extension with the bit-cell HFE, but the two formats are unrelated. This page
is the specification our reader and writer are built from. It was written from the behaviour and
the data layout of the HxC Floppy Emulator library, which is GPL and serves as our oracle only; no
code was taken. Every claim below is checked against `hxcfe` (HxCFloppyEmulator, commit
`b1f0465a`, v2.16.17.1) by `StreamHfeExternalConformanceTests`.

All integers are little-endian `uint32` unless noted.

## File header (offset 0, 512 bytes)

| Offset | Field | Notes |
| --- | --- | --- |
| 0x000 | signature, 16 bytes | ASCII `HxC_Stream_Image`, not terminated |
| 0x010 | format revision | 0 |
| 0x014 | flags | bit 0: double step |
| 0x018 | index array offset | in bytes; hxcfe writes a zeroed 512-byte block here and never reads it |
| 0x01C | track list offset | in bytes |
| 0x020 | track data offset | in bytes; where the first track's packed data starts |
| 0x024 | number of tracks | cylinders |
| 0x028 | number of sides | 1 or 2 |
| 0x02C | bit period | picoseconds per tick; hxcfe writes 40000 (25 MHz) |
| 0x030 | floppy interface mode | the HFE interface-mode codes (0 = IBM PC DD, ...) |
| 0x034 | reserved, 4 × uint32 | zero |
| 0x044 | internal name, 128 bytes | NUL-terminated |
| 0x0C4 | internal date, 16 bytes | NUL-terminated |
| 0x0D4 | padding up to 0x200 | zero |

hxcfe's writer places the track list at 0x200. It is padded to whole 512-byte blocks, with room for
`tracks × sides + 1` entries. The index block follows, then the track data.

## Track list

There is one 32-byte entry per (cylinder, side), indexed `cylinder × 2 + side` when the image has
two sides and `cylinder` when it has one:

| Offset | Field | Notes |
| --- | --- | --- |
| +0 | flags | bit 0: packed (LZ4) |
| +4 | packed data offset | in bytes from the start of the file |
| +8 | packed data size | bytes; 0 means the track is absent |
| +12 | unpacked data size | bytes of the pulse stream |
| +16 | track length | in ticks; one revolution |
| +20 | number of pulses | in one revolution |
| +24 | reserved, 2 × uint32 | zero |

hxcfe decompresses every track as a raw LZ4 block, whatever the flag says, and its writer always
sets the flag. Our reader honours the flag: an unpacked track is stored as is.

## Pulse stream

The pulse stream is a sequence of variable-length deltas. Each delta is the number of ticks from the
previous flux transition (or from the index, for the first one) to this transition. The deltas are
big-endian within each code:

| First byte | Length | Value |
| --- | --- | --- |
| `0xxxxxxx` | 1 | 7 bits |
| `10xxxxxx` | 2 | 14 bits |
| `110xxxxx` | 3 | 21 bits |
| `1110xxxx` | 4 | 28 bits |

The writer clamps a delta that is too large for 28 bits to `0x0FFFFFFF`. A first byte of `1111xxxx`
never appears. hxcfe skips it without consuming any further bytes, but still counts it as a pulse.
Our reader refuses a stream that contains one, because its meaning is undefined.

The stream covers one revolution of exactly `number of pulses` transitions. When hxcfe loads a
track, it replays the revolution three times. On each repeat, the gap between the last transition
and `track length` is added to the first delta of the next revolution. Track length is therefore
the revolution time, and it is at least the sum of the deltas.

## Measured, not assumed

- hxcfe round-trips a sector image through its own Stream HFE and decodes the original sectors
  (`hxcfe -finput:<img> -conv:HXC_STREAMHFE`, then `-conv:RAW_LOADER` back).
- hxcfe decodes the Stream HFE we write, from a track list and pulse streams we encode, to the same
  sectors. It also does so after one track has been replaced with another disk's.
