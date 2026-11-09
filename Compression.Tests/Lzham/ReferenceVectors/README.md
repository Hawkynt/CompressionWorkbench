# LZHAM reference vectors

Files written by LZHAM's own command-line tool. They are the test data for `LzhamReferenceTests`:
we decode each one, and we compress its input with the same parameters and must write the same
bytes.

| | |
|---|---|
| Written by | `lzhamtest -t0 <options> c <input> <output>` from `richgel999/lzham_codec` at `d379b1f9121e2197881c61cfc4713c78848bdfe7`, built for x86-64 with MinGW-w64 GCC against the static libraries (`LZHAM_USE_LZHAM_DLL 0`; `lzham_platform.cpp`'s own `sprintf_s`/`vsprintf_s` skipped because MinGW supplies them). `-t0` uses no helper threads, so the parse is deterministic. |
| Inputs | deterministic generators (random words, repeated lines, zeros, noise, 32-bit counters), mirrored in the test class and in `make-fixtures.py`; only their size and SHA-256 are kept |
| Checked | `lzhamtest -h<rate> d` returned each input before the file was saved |

`manifest.tsv` lists file, decoded size, SHA-256 of the decoded bytes, the lzhamtest options, the
Huffman table update rate the file needs and the file size. The rate is not stored in the file:
`lzhamtest d` must be told it with `-h`, and so must our decoder.

Regenerate with `python make-fixtures.py <lzhamtest>`.

| File | What it covers |
|---|---|
| `text-20k-m0-d20`, `text-20k-m2-d15`, `text-20k-m4-d20`, `repeats-60k-m3-d15` | fastest, default, better and uber levels; 32 KiB and 1 MiB dictionaries |
| `text-20k-m4-d20-h1`, `text-20k-m4-d20-h20` | the slowest and fastest Huffman table update rates |
| `repeats-300k-m4-d26`, `text-50k-m4-d28` (`-x`) | 64 MiB and 256 MiB dictionaries (larger match-slot alphabets), extreme parsing |
| `zeros-200k-m4-d20` | huge match lengths |
| `noise-3000-m4-d20` | a raw (stored) block |
| `one-byte-m4-d20`, `empty-m4-d20` | the smallest inputs |
| `mixed-188k-m0-d15` .. `mixed-188k-m4-d15`, `mixed-188k-m2-d16`, `mixed-188k-m1-d20` | every level on 188 000 bytes of lines, noise, words and counters: many 4 KiB blocks, a dictionary that wraps several times, the match finder's tie-breaks on near-repeats |
| `mixed-188k-m4-d16-x`, `mixed-188k-m4-d18-h3` | extreme parsing across wrapping blocks; a non-default table update rate |
