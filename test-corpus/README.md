# Real-World Test Corpus

This directory is for **real-world format validation** against the
CompressionWorkbench toolchain. It is picked up automatically by
`ExoticFormatReadmeTests.TestCorpus_IdentifyAndExtractAll`.

## Usage

Drop any archive, disk image, or compressed stream you want to validate into
this directory (or a nested subdirectory). Then run:

```bash
dotnet test Compression.Tests --filter "FullyQualifiedName~ExoticFormatReadmeTests"
```

The test will iterate every file, attempt to:

1. **Identify** the format via `FormatDetector.Detect`.
2. **List** its entries via `ArchiveOperations.List`.
3. **Extract** it to a temporary directory via `ArchiveOperations.Extract`.

A per-file report is written to the test output showing which stages succeeded
for each file. The test passes as long as at least one file in the corpus can
be read — unsupported formats contribute to the "error" column without causing
an overall failure.

## Ignored by Git

Everything in this directory except `README.md` is `.gitignore`d. This lets
contributors use private, licensed, or large files (Amiga disk images, retro
game archives, Mac StuffIt archives, etc.) without accidentally committing
them to the repository.

## Suggested sources

- **Amiga formats** (ADF, DMS, PackDisk, xMash, DCS) — Aminet, archive.org
  mirror of Fred Fish disks.
- **C64 formats** (D64, D71, D81, T64) — CSDb, archive.org.
- **Mac formats** (StuffIt, DiskDoubler, PackIt, MacBinary, BinHex) —
  Macintosh Garden, archive.org Mac abandonware.
- **Game archives** (GRP, HOG, BIG, WAD, PAK, BSA, VPK, MPQ, NSA, SAR) —
  any installed game's data directory.
- **Disk images** (MDF, NRG, CDI, BIN/CUE, ISO) — your own CD/DVD rips.
