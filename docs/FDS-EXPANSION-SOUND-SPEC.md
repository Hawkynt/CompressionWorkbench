# Famicom Disk System (RP2C33) expansion audio — implementation specification

Status: **implemented.** `Hawkynt.FileFormats.Audio/Codecs/Codec.Nes2a03/Expansion/FdsAudio.cs`
was rewritten clean-room from this document and the NESdev wiki page; §9 records where the
implementation follows the wiki over the text below, and the oracle evidence.

Why a replacement is needed: the current channel says its modulator phase handling, the
modulation-table step table, the pitch-modulation arithmetic and the master-volume divisors
"are ported from NSFPlay (`xgm/devices/Sound/nes_fds.cpp`)". NSFPlay carries no licence: its
maintainer's readme only *presumes* the original author meant it to be redistributable. Code
with no licence is incompatible with this LGPL-3.0-or-later repository (AGENTS.md, rung 2; see
[`LICENCE-PROVENANCE.md`](LICENCE-PROVENANCE.md)). Whoever writes the replacement must not have
read NSFPlay's `nes_fds.cpp` or the current `FdsAudio.cs`.

Everything below is hardware behaviour. The primary public description is the NESdev wiki page
**"FDS audio"** (`https://www.nesdev.org/wiki/FDS_audio`, rung 4 — community documentation
citing hardware tests); check each rule against it before implementing. Numeric constants are
facts of the chip and may be copied exactly.

---

## 1. Integration contract

The replacement implements the existing internal interface `IExpansionAudio`
(`Codec.Nes2a03/Expansion/IExpansionAudio.cs`) and is attached by `NsfPlayer` when the NSF
header's expansion byte has bit 2 (0x04) set:

| Member | Contract |
|---|---|
| `HandlesWrite(addr)` | true for `$4040`–`$408A` |
| `Write(addr, value)` | register write (§3) |
| `TryRead(addr, out value)` | `$4040`–`$407F` read back the wave RAM entry; everything else unhandled |
| `ClockOneCpuCycle()` | advance one CPU cycle (1.789773 MHz NTSC) |
| `Output()` | current level, linear, scaled into the 2A03 mixer's ≈0..1 domain (§6) |

The existing test hooks (`ModCounter`, `ModulationOffsetForTest()`, `WavePhase`, `WaveIndex`)
are used by the tests and should be kept or the tests adapted.

## 2. State

| State | Width | Reset |
|---|---|---|
| Wave RAM | 64 × 6-bit (0..63) | 0 |
| Modulation table | 64 × 3-bit entries (written in pairs, §3) | 0 |
| Wave accumulator | 22-bit; wave position = bits 21..16 | 0 |
| Mod accumulator | 22-bit; table position = bits 21..16 | 0 |
| Wave pitch, mod pitch | 12-bit each | 0 |
| Mod counter | 7-bit, signed in two's complement (−64..63) | 0 |
| Volume envelope, mod envelope | each: disable flag, direction, 6-bit speed, gain (0..63) | gain 0 |
| Master envelope speed | 8-bit | `$E8` (BIOS default) |
| Master volume | 2-bit | 0 |
| Flags | wave halt, mod halt, envelope halt, wave-RAM write enable | as written |

## 3. Registers

| Address | Bits | Effect |
|---|---|---|
| `$4040`–`$407F` | `--dd dddd` | wave RAM entry; written only while `$4089` bit 7 is set |
| `$4080` | `MDss ssss` | volume envelope: M = 1 disables the envelope and sets its gain to `s` directly; D = 1 increase, 0 decrease; `s` = speed. Resets the envelope's timer |
| `$4082` | `ffff ffff` | wave pitch bits 7..0 |
| `$4083` | `HE-- ffff` | wave pitch bits 11..8; H = 1 halts the wave and resets the wave accumulator to 0; E = 1 halts both envelopes (their timers reset; gains hold) |
| `$4084` | `MDss ssss` | modulation envelope, same layout as `$4080` |
| `$4085` | `-ccc cccc` | sets the mod counter directly |
| `$4086` | `ffff ffff` | mod pitch bits 7..0 |
| `$4087` | `H--- ffff` | mod pitch bits 11..8; H = 1 halts the modulator and clears the low 16 bits of the mod accumulator (the table position is kept) |
| `$4088` | `---- -ttt` | while the modulator is halted: write `t` to the table entry at the current position, advance the position by one, write `t` again, advance again — **each write fills two consecutive entries** (32 writes fill the table). Ignored while the modulator runs |
| `$4089` | `W--- --vv` | W = wave-RAM write enable (while set, the output holds its last value); `vv` = master volume |
| `$408A` | `ssss ssss` | master envelope speed. Resets both envelope timers |

## 4. Per-cycle behaviour

**Envelopes** (each of volume and modulation). An envelope is clocked only when it is not
disabled, the wave is not halted, envelopes are not halted (`$4083` bit 6) and the master
envelope speed is non-zero. Its period is `8 × (speed + 1) × masterSpeed` CPU cycles; at the
end of each period the gain moves one step towards 32 (increase) or 0 (decrease) and stops
there. A gain written directly through bit 7 can be as large as 63.

**Modulator.** Unless halted, each cycle adds the mod pitch to the mod accumulator. For every
table position the accumulator's bits 21..16 pass through during that addition (there can be
more than one), apply that position's entry to the mod counter:

| Entry | Effect on mod counter |
|---|---|
| 0 | +0 |
| 1 | +1 |
| 2 | +2 |
| 3 | +4 |
| 4 | reset to 0 |
| 5 | −4 |
| 6 | −2 |
| 7 | −1 |

Additions wrap within 7 bits. The accumulator then wraps to 22 bits.

**Pitch modulation.** With `pitch` = wave pitch (12-bit unsigned), `counter` = mod counter as a
signed value (−64..63) and `gain` = modulation envelope gain (0..63, *not* capped at 32):

1. `temp = counter × gain`; `remainder = temp & 0x0F`; `temp = temp >> 4` (arithmetic).
2. If `remainder > 0` and bit 7 of `temp` is clear: `temp −= 1` when `counter < 0`, else
   `temp += 2`.
3. While `temp ≥ 192`: `temp −= 256`. While `temp < −64`: `temp += 256`.
4. `temp = pitch × temp`; `remainder = temp & 0x3F`; `temp = temp >> 6`; if `remainder ≥ 32`,
   `temp += 1`.
5. The effective wave pitch is `pitch + temp`.

**Wave.** Unless halted, each cycle adds the effective wave pitch to the wave accumulator,
wrapping at 22 bits. A wave cycle (64 positions) therefore lasts `2^22 / pitch` CPU cycles:
`f = 1.789773 MHz × pitch / 4194304`.

## 5. Output level

`level = wave[position] × min(volumeGain, 32) × masterFactor[vv]`, with
`masterFactor = [2/2, 2/3, 2/4, 2/5]`. While the wave is halted the channel still outputs the
entry at position 0. While wave-RAM writes are enabled (`$4089` bit 7) the output holds the
last level computed before writes were enabled.

## 6. Mixing scale (a choice, not a chip fact)

The peak level is `63 × 32 × 1 = 2016`. Its loudness relative to the 2A03 is not a register
value; NESdev records measurements of the FDS output against the APU square channels, and the
current implementation maps the peak to ≈0.55 of the 2A03 mixer's full scale. The replacement
should keep that ratio (so existing NSF renders do not change level) unless a measurement says
otherwise, and record which it used.

## 7. Known divergences in the code being replaced

These are listed so the replacement's tests do not encode them by accident; each needs a
hardware-backed decision:

- One `$4088` write fills one table entry instead of two.
- `$4087` bit 7 does not clear the accumulator's low 16 bits.
- `$4083` bit 6 is treated as "use the volume gain directly" instead of halting both
  envelopes.
- The modulation gain is capped at 32 even when written directly (up to 63).

## 8. Reference vectors to capture

1. **Formula table** (no tool needed, derived from §4): `pitch × modulation-offset` results for
   counter ∈ {−64, −63, −1, 0, 1, 63}, gain ∈ {0, 1, 15, 16, 31, 32, 63} and pitch ∈ {0, 1, 0x7FF,
   0xFFF}; cross-check a sample of them against the NESdev pseudocode by hand and commit the
   table as test data.
2. **Register-write logs** from real FDS NSF rips (any NSF with expansion bit 0x04), rendered
   by a black-box emulator used only as an oracle — NSFPlay or Mesen (GPL) may be *run*, not
   read — at 1.789773 MHz without filtering; compare the per-cycle effective wave pitch and
   the output level sequence over the first few frames.
3. **Edge cases**: mod pitch 0 (modulator idle), wave pitch 0, master envelope speed 0
   (envelopes frozen), a mod-table pass containing entry 4 (counter reset), a mod counter
   written to 0x40 (−64) with gain 63 (the wrap in step 3), wave halted while writing wave RAM.

## 9. Decisions taken by the implementation

The rewrite checked every rule against the NESdev wiki page (revision 22987, September 2025).
Where the wiki's later findings disagree with the text above, the wiki was followed:

| Topic | Above | Implemented (wiki) |
|---|---|---|
| Unit clocking | per-cycle adds into 22-bit accumulators | wave and modulation units tick every 16 CPU cycles: 20-bit modulated pitch into a 24-bit wave accumulator (position = bits 23..18); 12-bit mod pitch into an 18-bit mod accumulator (table index = bits 17..13, bit 12 the "ghost" bit). Same rates as §4 |
| Pitch modulation | steps 1–5 of §4 (rounding after `>> 4`, wrap, rounding after `>> 6`) | the wiki's current pseudocode: `temp = counter × gain`; `+0x20` if the low nibble is non-zero and bit 11 is clear; `temp = ((temp + 0x400) >> 4) & 0xFF`; increment = `pitch × temp`. 64 means unmodulated; no second rounding |
| Envelope period | `8 × (speed + 1) × masterSpeed` | `8 × (speed + 1) × (masterSpeed + 1)`; master speed 0 still freezes both envelopes |
| Volume gain at the output | used directly | latched only while the wave position is 0 (PWM comparator latch); a direct write of gain 0 mutes at once |
| `$4040`–`$407F` reads | the addressed entry | the addressed entry while write-enabled, otherwise the entry at the current wave position; bits 7..6 read `01` (open bus) |
| `$4087` bit 6 | not described | forces a mod-table step on every tick |
| Mod step | "apply that position's entry" | the entry at the position being left is applied, then the position advances (the wiki's `$4095` description) |

Not implemented: the wiki's bit diagram for `$4083` also labels bit 7 "envelopes run 4× faster,
also stops the mod table accumulator", which contradicts its own text (envelopes stop while
the wave is halted); the text was followed. The analogue ≈2 kHz low-pass the wiki describes is
left to the mixer, so `Output()` stays the linear level of §5. The mixing scale of §6 was kept:
measured black-box against the replaced build, its peak (63 × 32, master volume 0) was exactly
0.55, linear, unfiltered — the rewrite reproduces that. All four §7 divergences are corrected.

Oracles: unit tests in `Compression.Tests/Codecs/Nes2a03/FdsAudioTests.cs` (the §8.1 formula
table, hand-walked; register, envelope, table and boundary behaviour) and
`FdsAudioOracleTests.cs`, which compares three homebrew FDS NSFs with Game Music Emu (ffmpeg's
libgme demuxer, run as a black box) on loudness contour and spectra. The only disagreement
found is that libgme stops applying the pitch-modulation formula while the modulator is halted;
the wiki says it stays in effect, and the implementation follows the wiki.
