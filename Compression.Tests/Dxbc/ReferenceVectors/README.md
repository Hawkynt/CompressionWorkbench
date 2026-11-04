# DXBC reference vectors

All written by Microsoft's `fxc.exe` from the Windows 11 SDK 10.0.26100.0 (`bin\10.0.26100.0\x64`),
compiling `tint.hlsl` (kept here as documentation).

| File | Command |
| --- | --- |
| `fxc-tint-ps50.dxbc` | `fxc -T ps_5_0 -E main -Fo fxc-tint-ps50.dxbc tint.hlsl` — RDEF, ISGN, OSGN, SHEX, STAT |
| `fxc-tint-ps40l91.dxbc` | `fxc -T ps_4_0_level_9_1 -E main …` — adds the Aon9 (Direct3D 9 bytecode) chunk, SHDR instead of SHEX |
| `fxc-tint-ps50-setprivate.dxbc` | `fxc -dumpbin fxc-tint-ps50.dxbc -setprivate priv.bin -Fo …` with `priv.bin` = `CompressionWorkbench private data` (33 bytes): fxc appends a PRIV chunk |
| `fxc-tint-ps50-stripreflect.dxbc` | `fxc -dumpbin fxc-tint-ps50.dxbc -Qstrip_reflect -Fo …`: fxc drops RDEF and STAT |

fxc refuses to disassemble a container whose checksum is wrong ("disassembly failed"), which makes
it the oracle for the checksum our writer computes.
