# Compression.UI

WPF archive browser and binary analysis wizard for CompressionWorkbench.

## Features

### Archive Browser
- Open/extract/create/test archives
- File list with icons, columns (name, size, compressed, ratio, method, modified)
- Column sorting, breadcrumb navigation into nested folders
- Preview window with text and hex views
- Properties dialog with compression ratio visualization
- Drag-and-drop file opening
- Explorer context menu integration via `Compression.Shell`

### Hex Viewer
- Byte-wise auto-width (adapts to window size, any column count)
- Manual override: 8, 16, 32, 64 bytes per row
- 8-byte grouping separators for readability
- Frequency-based byte coloring (in analyze mode):
  - Background: green (rare) -> neutral -> red (common)
  - Foreground: orange = control bytes, purple = high bytes

### Binary Analysis Wizard
Toolbar-driven analysis tools accessible via File > Analyze:

| Tool | Description |
|------|-------------|
| Scan Results | Magic bytes signature detection |
| Fingerprints | Statistical algorithm identification |
| Entropy Map | Per-region entropy with boundary detection |
| Trial Decompress | Automatic decompressor probing |
| Chain | Multi-layer compression reconstruction |
| Statistics | Full randomness/distribution analysis |
| Strings | ASCII/UTF-8/UTF-16 string extraction with search |
| Structure | Binary template parsing (`.cwbt` format) |

### Statistics Panel
Shared control reused in Preview, Properties, and Analysis windows:
- Randomness tests (entropy, chi-square, serial correlation, Monte Carlo pi)
- Byte distribution (unique bytes, most/least common)
- Content analysis (printable ASCII, control, high, null bytes)
- Interactive histogram with per-byte tooltips

## Building

```bash
dotnet build Compression.UI
# Requires: Windows with .NET 10 SDK (WPF)
```

## Dependencies
- `Compression.Lib` — format support
- `Compression.Analysis` — binary analysis engine
- WPF + Windows Forms (for folder dialogs)
