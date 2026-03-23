# Compression.CLI (`cwb`)

Universal command-line archive tool with smart conversion and optimal re-encoding.

## Installation

```bash
dotnet build Compression.CLI
# Binary: Compression.CLI/bin/Debug/net10.0/cwb.exe
```

## Commands

| Command | Alias | Description |
|---------|-------|-------------|
| `list <archive>` | `l` | List contents of an archive |
| `extract <archive> [files...]` | `x` | Extract files from an archive |
| `create <archive> <files...>` | `c` | Create a new archive |
| `test <archive>` | `t` | Test archive integrity |
| `info <archive>` | - | Show detailed archive information |
| `convert <input> <output>` | - | Convert between archive formats |
| `optimize <input> <output>` | `opt` | Re-encode with optimal compression |
| `benchmark <file>` | `bench` | Compare compression across algorithms |
| `analyze <file>` | - | Run binary analysis (signatures, entropy, fingerprinting) |
| `formats` | - | List all supported formats |

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
- `--sfx` / `--sfx-ui` — Create self-extracting archive

## Self-Extracting Archives

```bash
cwb create output.exe files/ --sfx           # Console SFX
cwb create output.exe files/ --sfx-ui        # GUI SFX
cwb create output.exe files/ --sfx-target linux-x64  # Cross-platform
```
