# Contributing

This guide covers how to build, test, and extend CompressionWorkbench.

---

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (pinned via `global.json`)
- Windows for the WPF UI (`Compression.UI`) and Shell extension (`Compression.Shell`)
- Any OS for the CLI, core library, and tests

---

## Build

```bash
dotnet build CompressionWorkbench.slnx
```

The solution uses the `.slnx` XML solution format. `Directory.Build.props` applies shared settings to all projects: `net10.0` target, C# 14, nullable reference types enabled, and warnings-as-errors.

---

## Test

### Full Test Suite

```bash
dotnet test
```

### External Tool Interop

Tests that verify our output is readable by 7z, gzip, bzip2, xz, zstd, lz4, and tar (and vice versa). Tests are skipped when tools are not found on PATH.

```bash
dotnet test --filter "Category=EndToEnd"
```

### OS Integration

Tests against native OS tools (PowerShell, mtools, genisoimage, qemu-img, etc.). Platform-specific tests are skipped on unsupported OSes.

```bash
dotnet test --filter "Category=OsIntegration"
```

---

## Continuous Integration

The repository uses GitHub Actions for build, test, coverage, and release automation. All workflow files live in `.github/workflows/`.

### Workflows

| Workflow | File | Trigger | Purpose |
|---|---|---|---|
| Build and Test | `build.yml` | push/PR to `main` | Cross-platform (Ubuntu + Windows) build and test matrix. Runs core tests unconditionally; E2E and OS integration tests run with `continue-on-error` after installing required tools. |
| Publish Release | `publish.yml` | tags matching `v*`, manual dispatch | Publishes self-contained single-file CLI (Windows + Linux) and UI (Windows only), uploading artifacts per platform. |
| Code Coverage | `coverage.yml` | push/PR to `main` | Runs core tests with `XPlat Code Coverage`, generates an HTML report via `dotnet-reportgenerator-globaltool`, and uploads it as an artifact. |
| Build and Release | `NewBuild.yml` | push/PR to `main`/`master` | Legacy end-to-end publish pipeline including SFX stub staging and GitHub Releases creation. |
| Tests | `Tests.yml` | push/PR to `main`/`master` | Legacy Windows-only full test run with coverage collection. |

Automated dependency updates are configured via `.github/dependabot.yml` for NuGet packages and GitHub Actions (weekly).

### Running CI Steps Locally

The same commands the workflows run are reproducible locally:

```bash
# Build and core test run (mirrors build.yml)
dotnet restore CompressionWorkbench.slnx
dotnet build CompressionWorkbench.slnx --configuration Release --no-restore
dotnet test Compression.Tests --configuration Release --no-build \
  --filter "Category!=EndToEnd&Category!=OsIntegration&Category!=ExternalInterop"

# Coverage (mirrors coverage.yml)
dotnet test Compression.Tests --configuration Release \
  --filter "Category!=EndToEnd&Category!=OsIntegration&Category!=ExternalInterop" \
  --collect:"XPlat Code Coverage" --results-directory ./coverage
dotnet tool install -g dotnet-reportgenerator-globaltool
reportgenerator -reports:./coverage/**/coverage.cobertura.xml \
  -targetdir:./coverage/report -reporttypes:HtmlInline_AzurePipelines

# Publish (mirrors publish.yml)
dotnet publish Compression.CLI/Compression.CLI.csproj \
  --configuration Release --self-contained --runtime linux-x64 \
  --output publish/cli -p:DebugType=none -p:GenerateDocumentationFile=false
```

For E2E and OS integration tests to run locally, install the external tools listed under the corresponding CI steps (`p7zip-full gzip bzip2 xz-utils zstd lz4 tar cpio genisoimage mtools qemu-utils` on Linux, `7zip` on Windows).

### Validating Workflow Changes

GitHub Actions workflows cannot be run locally with perfect fidelity, but `actionlint` is the recommended syntax linter:

```bash
# Install via Go, Homebrew, or prebuilt binary from github.com/rhysd/actionlint
actionlint .github/workflows/*.yml
```

---

## Publish

### CLI Tool (single-file, self-contained)

```bash
dotnet publish Compression.CLI -c Release --self-contained -r win-x64 -o publish/cli -p:DebugType=none -p:GenerateDocumentationFile=false
```

Replace `win-x64` with `linux-x64` or `osx-arm64` for other platforms.

### WPF UI (Windows only)

```bash
dotnet publish Compression.UI -c Release --self-contained -r win-x64 -o publish/ui -p:DebugType=none -p:GenerateDocumentationFile=false
```

---

## Adding a Format

Each file format lives in its own project at the repository root.

### Step 1: Create the Project

Create a new class library project `FileFormat.YourFormat`:

```bash
dotnet new classlib -n FileFormat.YourFormat -o FileFormat.YourFormat
```

The project inherits all settings from `Directory.Build.props` (net10.0, C# 14, nullable, warnings-as-errors, XML docs). Add references to the core libraries in the `.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\Compression.Core\Compression.Core.csproj" />
    <ProjectReference Include="..\Compression.Registry\Compression.Registry.csproj" />
  </ItemGroup>
</Project>
```

### Step 2: Implement the Descriptor

Create a class implementing `IFormatDescriptor` with your format's metadata:

```csharp
using Compression.Registry;

namespace FileFormat.YourFormat;

/// <summary>Format descriptor for the YourFormat file format.</summary>
public sealed class YourFormatDescriptor : IFormatDescriptor {
  /// <inheritdoc />
  public string Id => "YourFormat";
  /// <inheritdoc />
  public string DisplayName => "Your Format";
  /// <inheritdoc />
  public FormatCategory Category => FormatCategory.Archive; // or Stream, Filesystem, etc.
  /// <inheritdoc />
  public FormatCapabilities Capabilities => FormatCapabilities.Read | FormatCapabilities.Write;
  /// <inheritdoc />
  public string DefaultExtension => ".yf";
  /// <inheritdoc />
  public IReadOnlyList<string> Extensions => [".yf"];
  /// <inheritdoc />
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <inheritdoc />
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x59, 0x46], 0) // Magic bytes "YF" at offset 0
  ];
  /// <inheritdoc />
  public IReadOnlyList<FormatMethodInfo> Methods => [];
  /// <inheritdoc />
  public string? TarCompressionFormatId => null;
}
```

### Step 3: Implement Operations

For **compression streams**, implement `IStreamFormatOperations`:

```csharp
/// <summary>Stream operations for YourFormat.</summary>
public sealed class YourFormatStreamOps : IStreamFormatOperations {
  /// <inheritdoc />
  public void Decompress(Stream input, Stream output) {
    // Read compressed stream, write decompressed data
  }

  /// <inheritdoc />
  public void Compress(Stream input, Stream output) {
    // Read raw data, write compressed stream
  }
}
```

For **archive formats**, implement `IArchiveFormatOperations`:

```csharp
/// <summary>Archive operations for YourFormat.</summary>
public sealed class YourFormatArchiveOps : IArchiveFormatOperations {
  /// <inheritdoc />
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    // Parse archive directory, return entry list
  }

  /// <inheritdoc />
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    // Extract entries to outputDir
  }

  /// <inheritdoc />
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    // Write archive from input files
  }
}
```

### Step 4: Wire It Up

Add a `ProjectReference` in `Compression.Lib/Compression.Lib.csproj`:

```xml
<ProjectReference Include="..\FileFormat.YourFormat\FileFormat.YourFormat.csproj" />
```

Add the project to `CompressionWorkbench.slnx`.

The Roslyn source generator discovers the `IFormatDescriptor` implementation automatically at compile time. No manual registration is needed -- the CLI, UI, and format detection pipeline will all pick up the new format.

### Step 5: Add Tests

Add tests in `Compression.Tests`. At minimum, test round-trip (create then extract, verify data matches). If an external reference tool exists, add interop tests with `Assert.Ignore` when the tool is unavailable.

---

## Adding a Building Block

Building blocks are raw algorithm primitives without file format containers. They live in `Compression.Core` and are used for benchmarking.

### Step 1: Implement IBuildingBlock

Create a class in `Compression.Core/BuildingBlocks/`:

```csharp
using Compression.Registry;

namespace Compression.Core.BuildingBlocks;

/// <summary>Building block for the YourAlgorithm compression algorithm.</summary>
public sealed class BB_YourAlgorithm : IBuildingBlock {
  /// <inheritdoc />
  public string Id => "BB_YourAlgorithm";
  /// <inheritdoc />
  public string DisplayName => "Your Algorithm";
  /// <inheritdoc />
  public string Description => "Brief description of what this algorithm does";
  /// <inheritdoc />
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary; // or Entropy, Transform, etc.

  /// <inheritdoc />
  public byte[] Compress(ReadOnlySpan<byte> data) {
    // Compress raw bytes -- no container, no headers
  }

  /// <inheritdoc />
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    // Decompress raw bytes -- must round-trip with Compress
  }
}
```

The `Compress` and `Decompress` methods operate on raw `byte[]` data. Do not add file format headers or containers -- building blocks are algorithm primitives only.

### Step 2: Done

The source generator discovers `IBuildingBlock` implementations automatically. The benchmark tool (`cwb benchmark`) and benchmark tests will include the new block at the next build.

---

## Code Style

- **File-scoped namespaces**: `namespace FileFormat.YourFormat;` (not block-scoped)
- **C# 14**: Use latest language features (primary constructors, collection expressions, etc.)
- **Nullable reference types**: Enabled globally; annotate all public APIs
- **Warnings-as-errors**: The build fails on any warning; do not suppress warnings without justification
- **XML documentation**: Required on all public types and members (enforced by the build in library projects; the test project disables this)
- **Immutable headers**: File format header structures should be immutable record types
- **No emojis**: Do not use emoji characters in code, comments, or documentation files

---

## Testing Requirements

- All new code needs tests. At minimum, test the round-trip: compress then decompress (or create then extract) and verify the data matches.
- External interop tests should use `Assert.Ignore` (NUnit) when the required external tool is not available, so the test is skipped rather than failing.
- Test data should be deterministic (fixed seeds for random data, fixed strings for text).
- Do not commit large binary test files to the repository. Generate test data programmatically in setup methods.
