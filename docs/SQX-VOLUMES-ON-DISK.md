# SQX multi-volume archives

How an SQX archive is split over volumes, as `FileFormat.Sqx` reads it. Measured on sets Squeez
CommandLine 5.2 (Linux) writes with `-mv` (formats 1.1 and 2.0; stored, compressed and solid).

- The volumes of `name.sqx` are `name.sqx`, `name.001`, `name.002`, …; every one but the last has
  exactly the requested volume size (at least 130 KB).
- Each volume starts with its own main header. Main header flag `0x0008` marks a multi-volume
  set, `0x0010` its last volume; the byte after the format version (header offset 13) counts the
  volume from 0. Squeez also sets the solid flag `0x0004` on every set it splits.
- A file that does not fit is cut: its header in this volume carries flag `0x0010` ("continued in
  the next volume") and the compressed bytes that fit. The next volume begins with a file header
  for the same name and original size, holding the next piece (flag `0x0018` when it continues
  again; the last piece has neither bit). The pieces' compressed bytes, joined in order, are the
  file's one stored stream: stored data, or a single compressed stream that the cut does not
  restart.
- Every piece carries the whole file's original size; the last piece's CRC-32 is that of the
  whole file.
