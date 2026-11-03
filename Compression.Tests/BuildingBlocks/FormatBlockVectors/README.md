# BriefLZ, LZG and Lizard block vectors

The reference tools wrote these files. They were built with gcc 16.2:

- `blzpack.*.bin`: `blzpack` from jibsen/brieflz `ad89acf4186adca8ad8c27c4ab376a49ae233cd0` (zlib licence).
- `lzg.*.bin`: `lzg` from mbitsnbites/liblzg `c614d57fb5ba19681855df554b59826f1e1ef309` (zlib licence).
- `lizard.*.bin`: the `lizard` 2.1.0 CLI from inikep/lizard `d3becc7e80d3eb01147c2f63bed2e23fac533f19`.

Each tool compressed the two samples: `sample-text.bin` (24000 bytes of generated text) and `sample-small.bin` (`abracadabra abracadabra`). `BriefLzLzgLizardBlockTests` decodes every file through its building block, and the matching tool decodes what the block writes.
