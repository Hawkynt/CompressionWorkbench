# G.722 reference vectors

FFmpeg's `g722` encoder and decoder (LGPL-2.1-or-later) are the oracle. Every file here was
written by `make-vectors.sh` on 2026-10-23 with **FFmpeg 8.1.2** (`8.1.2-full_build-www.gyan.dev`,
Windows x64); `G722ReferenceVectorTests` asserts our encoder reproduces each `.g722` byte for byte
and our decoder reproduces each `.dec.pcm` sample for sample.

| Input (`*.pcm`) | Samples | Content |
| --- | --- | --- |
| `silence` | 16000 | 1 s of zeros |
| `sine1k` | 8000 | 1 kHz sine, amplitude 16384 (−6 dBFS) |
| `sine6k` | 8000 | 6 kHz sine, amplitude 16384 (high band dominant) |
| `noise` | 8000 | white noise: top 16 bits of a 32-bit LCG (×1664525 + 1013904223, seed 0x12345678) |
| `square` | 8000 | 400 Hz square wave, +32767 / −32768 |
| `speech` | 16000 | FFmpeg's `flite` filter (voice `slt`), "The quick brown fox jumps over the lazy dog.", first 1 s |
| `sweep.g722` | — | octets 0x00..0xFF repeated 64 times (decoder input only) |

All PCM is 16-bit signed little-endian, mono, 16 kHz, headerless.

## Commands

```sh
ffmpeg -f s16le -ar 16000 -ac 1 -i <name>.pcm -c:a g722 -f g722 <name>.g722
ffmpeg -f g722 -i <name>.g722 -f s16le <name>.dec.pcm
ffmpeg -f g722 -i sweep.g722 -f s16le sweep.dec.pcm
```

`docs/G722-SPEC.md` §7 passes `-ar 16000` to the decoding command as well; FFmpeg 8.1.2's `g722`
demuxer rejects that option ("Option sample_rate not found") and always reads at 16 kHz, so it is
left out.

To regenerate: `FFMPEG=/path/to/ffmpeg PYTHON=python3 ./make-vectors.sh`.

## SHA-256

| File | SHA-256 |
| --- | --- |
| `silence.pcm` | `0c92bddb4e96f3ea9ec9f0f64a668255a6c15527ac09f6f119cafde60c7c4a39` |
| `silence.g722` | `9c0b9b2330c2fa6b38a5799a186bfcec138aa6445a5c4dd2d5e44b70897271c7` |
| `silence.dec.pcm` | `67f04dfd3801f7a0b3f52d52e00fcb9df8190acf4b9ead906a2c7cb320c8cfc8` |
| `sine1k.pcm` | `dbac6b2f7287e7bd4599ef6e5436144eb37959c8e1ad4e45ef38f299ab247ef5` |
| `sine1k.g722` | `2331369d89a30871d8fd52a515137eced5f104cc4bd9cf518211dd2aec16cd3d` |
| `sine1k.dec.pcm` | `2a178e0c735501a471d874404be2bd38c8681b5afa37db47775f8f2e8ac218d0` |
| `sine6k.pcm` | `422b9e96583751e10b1aafd4df73e6d9838c10922780b35ef7482ea107d4e687` |
| `sine6k.g722` | `d1b6be314a5b2504d7a0e39089ec281fdfbfa5fc9f911401d211142e5dda5401` |
| `sine6k.dec.pcm` | `9749cd55ed81825c023c8e666fcd1d5aa1e1b9a762a9c591b99627aa73360f42` |
| `noise.pcm` | `d4921eec48e2dca25ae0b4d91a2fd18eae20612dfe7590cc9deacd5b4f6cc125` |
| `noise.g722` | `100cebb0e46fcf9cd0b77c8a16e1cb61834682b2f11314c75f6616fc0121e791` |
| `noise.dec.pcm` | `c4261b49e4cd631ea5628ef402c15bed32e6a84c54913cc1595e5fc9305e2edc` |
| `square.pcm` | `8e4ebaaf9994e1afa3078c0b9b30eae51b12d7efa9a82572272bb4fb3fe15455` |
| `square.g722` | `a999e8bd74a6062e1d0786b27c41539fb626541e2a0865aa573f91345f45ef7c` |
| `square.dec.pcm` | `351e2bddba81e72677f321af6c2e16bf14833028225fb80e5f7084d748199e05` |
| `speech.pcm` | `fd42494a02a7876e3357b5c4541bb01cfbb3c5dd82da91dcda42cf75329235c9` |
| `speech.g722` | `3e02adb7a04f2d5252c64404632f54c73d6c195655840c59bb3472fc27b42f00` |
| `speech.dec.pcm` | `5b4ed856d72734753178be3ae342799f9f29d043e7b1b68e1e81d1f57da683fc` |
| `sweep.g722` | `a1f259d4365ed4320c377ce26f5c8c56dcdc9a89e7b641bfd8eabfbbeac86654` |
| `sweep.dec.pcm` | `3e6d4e8f4f0ddf0ac7559984a466ddb5ffa627a6fc981900ad7ff1d73cd74e83` |

The ITU-T G.722 Appendix II digital test sequences (§7.1 of the spec) are not included here.
