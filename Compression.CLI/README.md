# Compression.CLI (`cwb`)

Universal command-line archive tool with smart conversion and optimal re-encoding.

## Installation

```bash
dotnet build Compression.CLI
# Binary: Compression.CLI/bin/Debug/net10.0/cwb.exe
```

## Commands

### Reading and writing archives

| Command | Alias | Description |
|---------|-------|-------------|
| `list <archive>` | `l`, `ls` | List contents of an archive |
| `extract <archive> [files...]` | `x` | Extract files from an archive |
| `create <archive> <files...>` | `c` | Create a new archive |
| `test <archive>` | `t` | Test archive integrity |
| `add <archive> <files...>` | - | Add or replace files inside an existing archive |
| `remove <archive> <names...>` | `rm` | Remove named entries from an existing archive |
| `replace <archive> <entry> <file>` | - | Replace a single entry with a new file |
| `info <archive>` | - | Show detailed archive information |
| `inspect <file>` | - | Inspect file-specific capabilities (e.g. `--unpack-capabilities`) |

### Conversion and re-encoding

| Command | Alias | Description |
|---------|-------|-------------|
| `convert <input> <output>` | - | Convert between any formats (archive, FS, stream) |
| `convert-archive <in> <out>` | - | Cross-format conversion (archive↔archive, archive↔FS, FS↔FS). `convert-fs` is a hidden back-compat alias |
| `optimize <input> <output>` | `opt` | Re-encode with optimal compression |
| `bestfit <file>` | - | Rank every building block on the file's data; `--apply` writes the winner's output |
| `benchmark <file>` | `bench` | Compare compression across algorithms |
| `formats` | - | List all supported formats |
| `suggest <file>` | - | Platform-aware format recommendation |

### Analysis

| Command | Alias | Description |
|---------|-------|-------------|
| `analyze <file>` | - | Run binary analysis (signatures, entropy, fingerprinting) |
| `auto-extract <file>` | - | Recursive nested extraction (disk -> partition -> FS -> file) |
| `batch <dir>` | - | Scan a directory and aggregate format stats |
| `carve <file>` | - | Photorec-style file carver |
| `visualize <file>` | - | Colored block map of detected envelopes |
| `reverse-engineer <tool>` | `reveng` | Black-box probing of an unknown compression tool |
| `tool (init\|list\|add\|run\|remove)` | - | Manage and run external-tool templates |

### Image maintenance

| Command | Alias | Description |
|---------|-------|-------------|
| `defragment <image>` | `defrag` | Defragment a FS image in place |
| `scramble <image>` | - | Scatter every block on purpose, so the defragmenter has something to do. Content is preserved exactly |
| `place <image> <name>` | - | Put one named file at one chosen offset, moving whatever is in the way |
| `shrink <image>` | - | Defrag + truncate trailing free space |
| `wipe-empty <image>` | `wipe` | Zero-fill all unused space in an image or archive |
| `compact <image>` | - | Defragment + optimize + shrink: the smallest still-valid container |
| `reconfigure <image>` | - | Change geometry/options after creation without losing data; verified before the original is replaced |
| `convert-clusters <image>` | - | Rebuild a FAT image with a different cluster size |
| `resize <image>` | - | Resize a filesystem image to a target size |
| `dedup <image>` | - | Find and optionally remove duplicate files (by SHA-256) |
| `sparsify <image>` | - | Remove zero-filled blocks from a container image |
| `densify <image>` | - | Pre-allocate all blocks in a container image |
| `deploy <image> <device>` | - | Raw-write an image to a block device with CRC verification |

### Partition table

`cwb partition <sub>` edits the MBR/GPT table of a raw disk image or a
virtual-disk container (VHD/VHDX/VMDK/QCOW2/VDI).

| Subcommand | Description |
|---|---|
| `list` | Show all primaries + logicals in disk-table order |
| `add` | Add a primary, or a logical inside an extended container |
| `delete` | Remove a partition entry; bytes left untouched |
| `purge` | Remove the entry **and** zero-fill the partition's bytes |
| `convert` | Switch between MBR and GPT schemes |
| `format` | Write a fresh filesystem image into a partition |
| `verify` | Check signature, GPT header/entry-array CRCs, primary/backup consistency, extent bounds |

The verbs `compact`, `defragment`, `shrink`, `wipe-empty` and `purge` are the
maintenance set defined once in [`docs/ARCHIVE-MODEL.md`](../docs/ARCHIVE-MODEL.md);
which formats offer which is recorded in the package READMEs, not here.

## Examples

```bash
cwb list archive.zip
cwb extract archive.7z -o ./output
cwb x archive.rar -p mypassword
cwb create output.zip myDir file1.txt *.txt
cwb create output.7z file.txt --method lzma2+
cwb convert input.tar.gz output.tar.xz
cwb optimize input.zip optimized.zip
cwb benchmark largefile.bin
cwb analyze unknown.bin
cwb auto-extract sample.vhd --recursive
cwb defragment disk.img --mode pack-start
cwb shrink disk.img
cwb wipe-empty disk.img
cwb convert-archive disk.d64 output.zip     # retro FS to modern archive
cwb convert-archive archive.zip out.tar     # archive to archive
cwb convert-archive archive.zip out.img -f fat # archive to filesystem image
cwb dedup disk.img --dry-run
cwb sparsify disk.vhd
cwb deploy disk.img \\.\PhysicalDrive2 --yes
cwb suggest big.csv
```

## Method+ System

Append `+` to any method for optimal encoding:

| Method | Optimal variant |
|--------|----------------|
| `deflate+` | Zopfli optimal Deflate |
| `lzma+` | Best LZMA |
| `zstd+` | Best Zstandard |
| `brotli+` | Best Brotli |
| `lz4+` | HC maximum |
| `lzw+` | Optimal LZW |
| `lzo+` | LZO1X-999 |

## Fine-Tuning Options

- `--dict-size SIZE` — Dictionary size (e.g. 64k, 8m, 64m)
- `--word-size N` — Word size / fast bytes / model order
- `--level N` — Compression level 0-9
- `--threads N` — Parallel compression threads
- `--solid-size SIZE` — 7z solid block size
- `--force-compress` — Override incompressibility detection
- `--sfx` / `--sfx-ui` — Create a self-extracting archive. `--sfx` is a console stub; `--sfx-ui`
  shows a small window with a folder picker. Both are NativeAOT binaries, so the recipient needs
  nothing installed.
- `--sfx-target RID[,RID...]` — Which runtimes to build for. Several, comma-separated, produce one
  file that runs on all of them.

## Self-Extracting Archives

```bash
cwb create output.exe files/ --sfx                     # console stub, this machine's runtime
cwb create output.exe files/ --sfx-ui                  # windowed stub with a folder picker
cwb create output.exe files/ --sfx-target linux-x64    # build for another runtime
cwb create output.exe files/ --sfx --sfx-target win-x64,linux-x64,osx-arm64
```

**Nothing to install.** The stubs are compiled ahead of time to native code, so an SFX runs on a
machine with no .NET on it. Targets: `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`,
`osx-arm64`. There is no x86 target — NativeAOT has none — and no musl build.

**Size.** A stub carved for one format is 0.9–2.1 MB depending on how much codec it needs (tar is
the smallest, RAR the largest). Formats outside the carved set fall back to a universal stub of
about 11.6 MB that reads every archive format the toolkit supports. Nothing is unsupported; only
the size differs.

**One file for several systems.** Listing more than one target produces a single file that is both
a Windows executable and a POSIX shell script. Windows runs it directly. On Linux and macOS invoke
it as `sh output.exe` — the first bytes of the file have to be `MZ` for Windows, which leaves no
room for a `#!` line, so the kernel cannot launch it on its own.

**It is still an ordinary archive.** The payload is stored verbatim at the end of the file, so the
tool that owns the format can open it without running our stub:

```bash
unzip -l output.exe                # lists the entries
unzip output.exe readme.txt        # pulls out a single file
7z e output.exe readme.txt         # likewise
```

This works for formats whose readers tolerate leading data — anything that locates itself by a
signature scan or an end-anchored directory. It does not work for `tar`, which has neither.
