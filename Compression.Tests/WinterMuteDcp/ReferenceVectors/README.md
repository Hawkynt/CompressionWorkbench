# WinterMute DCP reference vectors

| File | Origin |
| --- | --- |
| `packmaker-v1.dcp` | dead:code's PackMaker (`src/utils/dcpatch/PackMaker.exe` in the WME 1.x source tree, revision 663d154b, run on Windows 11) from `data/a.txt` (`hello dcp\n`), `data/sub/b.bin` (bytes 0..255 sixteen times) and `data/sub/s.wav` (`RIFFxxxxWAVE`, kept uncompressed by an `Uncompressed` filter on `*.wav`), description `CWB test package`: a version 0x100 package. |
| `packmaker-v1-engine.dcp` | the same PackMaker from the files of `EngineInputs()` in `WinterMuteDcpTests.cs`; the WME 1.10.1 engine ran it (logging the 0x100 package as "outdated" but loading it) and its script wrote exactly `wme-result.txt`. |
| `cwb-engine.dcp` | written by `WinterMuteDcpFormatDescriptor.Create` (defaults) from `EngineInputs()`: a version 0x200 package. |
| `wme-result.txt` | `result.txt` written by the game script inside `cwb-engine.dcp` when the WME 1.10.1 engine (`wme.exe` and `dcscomp.dll` from `wme_devkit_1101.exe`, archive.org item `wintermute_engine`) ran it as `data.dcp`: a compressed text file, a binary file checksummed byte by byte, a missing file. |
| `dcp-unpacker-listing.txt` | SHA-256 of every file dcp_unpacker (michaelwright235/dcp_unpacker, MIT) extracted from `cwb-engine.dcp`. |

The engine needs a `wme.ini` next to `wme.exe` that preselects the machine's devices so it shows no
dialog (`[System] ShowSettings=0, LastRun=1`, `[Video] DeviceGuid=<the D3D adapter GUID>, Windowed=1,
Accelerated=1`, `[Audio] NoSound=1`); with that, `CWB_WME_DIR` repeats the engine run live.

Not committed (third-party game data): the freeware WME game *War* (archive.org item `war_wme`,
`War/data.dcp`, 32 591 890 bytes, a ProjectMan-built 0x200 package with 634 files). Our reader and
dcp_unpacker extract identical bytes for all 634 files; `CWB_DCP_REAL` repeats the read.
