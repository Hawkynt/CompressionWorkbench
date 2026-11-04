# UM3 / UTAGE reference vectors

| File | Origin |
| --- | --- |
| `tone.ogg` | FFmpeg 8.1.2 (`-f lavfi -i sine=frequency=440:duration=0.25:sample_rate=22050 -ac 1 -c:a libvorbis -q:a 0 -fflags +bitexact -map_metadata -1`). |
| `t.png` | a 4x4 RGB PNG built by a few lines of Python (zlib + CRC chunks). |
| `t.jpg` | FFmpeg 8.1.2 (`-f lavfi -i testsrc=size=8x8 -frames:v 1`, bitexact). |
| `cwb-tone.um3` | written by `Um3FormatDescriptor.Compress` from `tone.ogg`. |
| `cwb-t.png.utage`, `cwb-t.jpg.utage` | written by `UtageFormatDescriptor.Compress` (default key) from `t.png` / `t.jpg`. |
| `garbro-decode.txt` | GARbro 1.5.44's decode of each file: for audio its `AudioFormat.Read` (format, rate, channels, PCM length, SHA-256 of the PCM), for images its `ImageFormat.Read` (size, SHA-256 of the BGRA32 pixels). Produced with a small .NET Framework driver around `GameRes.dll` / `ArcFormats.dll` (its source is in `GarbroOracle.cs.txt`). |

GARbro decodes each `cwb-*` file to exactly what it decodes from the plain original, and the tests
require our encoders to reproduce the `cwb-*` files byte for byte.
