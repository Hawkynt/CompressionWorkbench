# RAR 2.9 stream: what libarchive does not cover

`Rar3Decoder` converts libarchive's RAR reader (BSD-2-Clause). Three behaviours are
missing from that reader. They were written down here from reading unrar 7.0.9
(`rarvm.cpp`, `unpack30.cpp`). That is sourcing rung 2: the unRAR licence rules out
taking its code. They were implemented from this page.

## End of a file's data, and solid archives

Main-code symbol 256 is followed by one bit:

- `1`: new tables follow, in the same file.
- `0`: the file's data ends here. One more bit follows: `1` if the next file of a
  solid archive starts with new tables, `0` if it carries on with the current ones.

A decoder that stops once it has reached the file's size must still read this marker.
Otherwise the next solid file does not know whether tables follow. A non-solid file
always begins with tables.

PPMd blocks end a file with the escape byte followed by 2. That does not change
whether the next solid file reads tables. It reads them only if the previous LZ table
read or end marker left none current. A file that was PPMd from the start therefore
makes the next solid file read tables again.

## Reading LZ tables resets the low-offset state

Every LZ table read sets the last low offset and the low-offset repeat count to 0.

## IA-64 filter

The RarVM program is 120 bytes long with CRC-32 `0x3769893F`. It processes a block
of N bytes (21 <= N <= 0x3C000), starting at file offset F, in 16-byte bundles while
fewer than 21 bytes from the end remain unvisited. The bundle counter starts at
F >> 4 and increments once per bundle.

For each bundle:

1. Let `t = (byte0 & 0x1F) - 0x10`. Skip the bundle if `t < 0`.
2. Look up the slot mask `{4,4,6,6,0,0,7,7,4,4,0,0,4,4,0,0}[t]`.
3. For every slot `i` (0-2) whose bit is set, let `p = 41*i + 5`.
4. If the 4-bit field at bit `p + 37` equals 5, replace the 20-bit field at bit
   `p + 13` with `(field - bundle counter) & 0xFFFFF`.

Bit fields are little-endian within the bundle: the field at bit `b` is read from the
32-bit little-endian word at byte `b >> 3`, shifted right by `b & 7`.

No archive in the test set uses this filter, because RAR 6.24 left synthetic bundles
unfiltered. It is implemented but unproven.
