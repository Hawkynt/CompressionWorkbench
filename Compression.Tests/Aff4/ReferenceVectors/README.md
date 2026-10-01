# AFF4 logical reference images

Every file here was written by pyaff4 (https://github.com/aff4/pyaff4, the AFF4 reference
implementation, master `6a91158661edec6ed8a865a09e28dbf30d487e38`), never by CompressionWorkbench.

| File | Bytes | Origin |
| --- | --- | --- |
| `dream.aff4` | 4542 | pyaff4 `test_images/AFF4-L/dream.aff4`, unmodified: the AFF4-L sample of the original AFF4-L paper, also archived in https://github.com/aff4/ReferenceImages under `AFF4-L/deprecated/` |
| `pyaff4-logical-snappy.aff4` | 46207 | written by `generate_pyaff4_fixtures.py`: Deflate ZipSegments plus a Snappy ImageStream over three bevies |
| `pyaff4-logical-zlib-stored.aff4` | 43143 | written by `generate_pyaff4_fixtures.py`: Stored ZipSegments plus a zlib ImageStream |
| `pyaff4-expected.tsv` | 1309 | pyaff4 reading the three images back: image, logical name, size, SHA-256 |

The two generated images were written on Windows with Python 3.8, so their logical names use `\`
separators, as any pyaff4 image made on Windows does. The generator lowers pyaff4's ZipSegment limit
and its chunks per bevy so that a 100 KB file covers what a large one does: a raw stored chunk,
compressed chunks, a padded final chunk and several bevies. The source tree is rebuilt from a seeded
random generator, so `stream.bin` (SHA-256 `6e4e7ea8...c5db61bc`) can be recomputed without pyaff4.

pyaff4's whole-stream read agrees with that recomputation. Its `LinearHasher2`, which reads in 32 KiB
steps, misreports `stream.bin` in both generated images as a hash mismatch; the defect is in that read
path of pyaff4, not in the images, and the listing above comes from the whole-stream read.

`dream.aff4` is redistributed under the Apache License, Version 2.0
(https://www.apache.org/licenses/LICENSE-2.0) as part of pyaff4 ("Copyright 2014 Google Inc. All
rights reserved." and later contributors). The generated images are pyaff4's output for inputs made
by the generator script, which belongs to this repository.
