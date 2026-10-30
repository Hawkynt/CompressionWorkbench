# FDS expansion-sound test NSFs and libgme captures

The three NSFs are homebrew test tunes written for this repository (public domain).
`make_fds_nsf.py` builds them: each carries a small hand-assembled 6502 driver that replays one
frame of `$40xx` register writes per play call from a script table. The header sets expansion
bit 2 (FDS), NTSC, load/init `$8000`, play `$8010`.

| File | What it drives |
|------|----------------|
| `fds_tone.nsf` | sine wave at pitches `$100`, `$2A0`, `$4F3`, `$7FF`, `$C00`, `$FFF`, then master volume 0..3, then wave halt |
| `fds_envelope.nsf` | volume envelope up and down at two speeds, master envelope speed `$40` and `0`, envelopes halted by `$4083` bit 6 and released |
| `fds_modulation.nsf` | vibrato table at mod gain 16, direct gain 63, mod envelope rising from 0, halted modulator with counter 63 / −64, a table holding reset entries |

`capture_gme_features.py` renders each NSF with Game Music Emu through ffmpeg's libgme demuxer
(ffmpeg 8.1.2, gyan.dev full build, Windows; captured 2026-10-30) at 44100 Hz mono for 12 s and
stores the analysis the tests compare against:

- `<tune>.gme.tsv`: per window (4096-sample Hann, hop 2205), the window centre and its RMS;
- `<tune>.gme.spec`: per window, FFT magnitudes for bins 1..463 (≈11 Hz..5 kHz), scaled so
  the window's largest bin is 255, one byte per bin.

libgme stops `fds_tone.nsf` after about 6 s of silence detection, so that capture has fewer
windows. libgme is a black-box oracle only; its source was not read. `FdsAudioOracleTests`
explains the comparison rule and the tolerances, and why one stretch of `fds_modulation.nsf`
is left out of the comparison.
