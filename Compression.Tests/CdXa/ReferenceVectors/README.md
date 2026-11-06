# CD-XA reference vector

`AVSEQ09-head.dat` is the first 34 sectors of `AVSEQ09.DAT`, the RIFF CDXA copy of track 9 of a
Video CD. The Internet Archive item `avseq-07_20220223` publishes it at
<https://archive.org/download/avseq-07_20220223/AVSEQ09.DAT>. The complete file is 27,431,420 bytes
(11,663 sectors) and has SHA-256
`d4344c4650300fb1e7e00bdc2720d4f3d76db3961ae5decc0d7e97834d7581b7`.

The RIFF size at offset 4 and the data size at offset 40 were set to match the cut. Nothing else
was changed. The 34 sectors are the 30 empty lead sectors (file 9, channel 0, submode 0x60), then
the first four MPEG packs. The first two carry only a system header and padding, one marked as video
(0x62) and one as audio (0x64). They are followed by a video pack and an audio pack.

`CdXaExternalConformanceTests` uses the complete file. CI downloads it from the address above and
checks its hash.
