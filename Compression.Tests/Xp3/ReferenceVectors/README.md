# XP3 reference vectors

Inputs, unless stated otherwise: `a.txt` (`hello xp3\n`), `one.txt` (`z`), `sub/b.bin` (bytes 0..255
sixteen times), `sub/dup.txt` (same bytes as `a.txt`), `sub/umlaut-ä.txt` (`kirikiri ` fifty times).

| File | Origin |
| --- | --- |
| `rel-default.xp3` | KiriKiri 2 Releaser (`kr2_232r2.zip` from krkrz.github.io, `tools/krkrrel.exe`, Windows 11), `krkrrel proj -out rel-default.xp3 -nowriterpf -go` with its default profile: 2.32 header, zlib index, `.txt` compressed, `.bin` stored, `dup.txt` sharing `a.txt`'s segment. |
| `rel-stored-raw.xp3` | the same Releaser with a profile setting `CompressIndex=0`: raw index. |
| `rel-raw-protect.xp3` | the same Releaser with `CompressIndex=0` and `Protect=1`: every member carries the protect flag, and the archive starts with the Releaser's warning PNG and a banner entry whose name is the warning text. |
| `garbro-v1.xp3` | GARbro 1.5.44 (`GameRes.dll` / `ArcFormats.dll`, `Xp3Opener.Create`, Version=1, nothing compressed) from `a.txt`, `empty.txt` (0 bytes) and `sub/b.bin`: the pre-2.32 header. |
| `garbro-v2-zlib.xp3` | the same inputs, Version=2, contents and index compressed. |
| `cwb-v2-zlib.xp3` | written by `Xp3FormatDescriptor.Create` with default options from the Releaser inputs above. |
| `cwb-v1-raw-stored.xp3` | the same, `method=store`, `index=raw`, `version=1`. |
| `cwb-engine.xp3` | written by `Xp3FormatDescriptor.Create` from the `EngineInputs()` of `Xp3Tests.cs`: a `startup.tjs` that reads members back through the engine, plus four members. |
| `garbro-listing.txt` | GARbro 1.5.44's view of every `.xp3` above (name, size, packed/stored, SHA-256 of what it extracts), produced with `GarbroOracle.cs.txt` (a small .NET Framework driver for GARbro's `ArcFile.TryOpen`, compiled with the framework's `csc.exe`). |
| `krkrz-result.txt` | the KiriKiri Z 1.4.0 engine (`krkrz_20171225r2.7z`, `tvpwin32.exe`) started next to `cwb-engine.xp3` renamed to `data.xp3`; this is the `result.txt` its `startup.tjs` wrote, re-saved as UTF-8. |

The tests require our writer to reproduce the three `cwb-*.xp3` files byte for byte, so the GARbro and
engine verdicts recorded here stay attached to what the writer produces. `Given_OurArchive_When_KiriKiriZRunsIt_Then_EngineReadsEveryMember`
repeats the engine run live when `CWB_KRKRZ` names `tvpwin32.exe`.

The Releaser could not archive a file with a Japanese name on this (German-locale) machine; it hangs, so
the non-ASCII name in these fixtures is Latin-1.
