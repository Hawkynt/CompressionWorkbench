#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.AppleSparse;

/// <summary>
/// Descriptor for Apple sparseimage (<c>hdiutil create -type SPARSE</c>)
/// containers. The format is a 4 096-byte header with the <c>sprs</c>
/// magic at byte offset 0 followed by physical bands of
/// <c>sectors_per_band * 512</c> bytes each, addressed by a band-table
/// in the back half of the header.
///
/// <para>This descriptor surfaces each allocated logical band as a
/// synthetic <c>band-NNNN.bin</c> entry. Inner HFS+/APFS extraction is
/// delegated to <c>FileSystem.HfsPlus</c> / <c>FileSystem.Apfs</c> — the
/// descriptor is intentionally scoped to band-level operations.</para>
/// </summary>
public sealed class AppleSparseFormatDescriptor :
  IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable {

  public string Id => "AppleSparse";
  public string DisplayName => "Apple sparseimage";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;

  public string DefaultExtension => ".sparseimage";
  public IReadOnlyList<string> Extensions => [".sparseimage"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("sprs"u8.ToArray(), Offset: 0, Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Apple sparseimage — 4 KB header + 'sprs' magic + band-allocation table; " +
    "allocated bands surface as band-NNNN.bin synthetic entries. Inner HFS+/APFS " +
    "mutation is delegated to the respective filesystem descriptors.";

  // ── IArchiveFormatOperations ────────────────────────────────────────

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var container = AppleSparseReader.TryRead(stream);
    if (container == null) return [];
    return container.Bands
      .Select((b, i) => new ArchiveEntryInfo(
        i,
        AppleSparseReader.FormatBandName(b.LogicalBandIndex),
        b.Size,
        b.Size,
        "stored",
        false,
        false,
        null))
      .ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var container = AppleSparseReader.TryRead(stream);
    if (container == null) return;
    foreach (var band in container.Bands) {
      var name = AppleSparseReader.FormatBandName(band.LogicalBandIndex);
      if (files != null && files.Length > 0 && !MatchesFilter(name, files)) continue;
      var bytes = AppleSparseReader.ReadBand(stream, band);
      WriteFile(outputDir, name, bytes);
    }
  }

  // ── IArchiveCreatable ───────────────────────────────────────────────

  /// <summary>
  /// Builds a fresh sparseimage from <paramref name="inputs"/>. Inputs
  /// whose <c>ArchiveName</c> matches the <c>band-NNNN.bin</c> schema are
  /// allocated as logical bands; non-matching inputs are dropped (this is
  /// a band-level writer, not a filesystem-level one). The default
  /// geometry is 1 MiB bands (sectors_per_band = 2 048) with capacity for
  /// 256 logical bands.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);

    var sectorsPerBand = 2048;
    var maxLogicalBands = 256;

    // Build the empty container in memory, write it, then route inputs through
    // the in-place modifier so allocation and band-table updates take the same
    // path as the mutation API.
    var header = AppleSparseInPlaceModifier.BuildEmptyContainer(sectorsPerBand, maxLogicalBands);
    output.Position = 0;
    output.SetLength(0);
    output.Write(header);

    if (!output.CanSeek)
      throw new NotSupportedException("AppleSparse output stream must be seekable.");

    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      if (!AppleSparseInPlaceModifier.TryParseBandEntryName(input.ArchiveName, out var logical))
        continue;

      var data = input.ReadContent();
      var bandSize = sectorsPerBand * AppleSparseReader.SectorBytes;
      if (data.Length == bandSize) {
        AppleSparseInPlaceModifier.WriteBand(output, logical, data);
        continue;
      }
      // Right-pad/truncate to exact band size so callers don't need to know it.
      var sized = new byte[bandSize];
      var copy = Math.Min(data.Length, bandSize);
      data.AsSpan(0, copy).CopyTo(sized);
      AppleSparseInPlaceModifier.WriteBand(output, logical, sized);
    }
  }

  // ── IArchiveModifiable ──────────────────────────────────────────────

  /// <summary>
  /// Rewrites logical bands in place. Inputs whose <c>ArchiveName</c>
  /// matches <c>band-NNNN.bin</c> are written at the fixed byte offset
  /// <c>HeaderSize + (band_table[logical] - 1) * band_size</c>; inputs
  /// whose schema doesn't match are skipped — inner HFS+/APFS mutation
  /// is delegated to those filesystem descriptors.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);

    var container = AppleSparseReader.TryRead(archive)
      ?? throw new InvalidDataException("Stream is not an Apple sparseimage container.");
    var expectedSize = container.BandSize;

    AppleSparseInPlaceModifier.AddOrReplaceBands(archive,
      inputs.Where(i => !i.IsDirectory).Select(i => {
        var data = i.ReadContent();
        if (data.Length == expectedSize) return (i.ArchiveName, data);
        // Pad/truncate to band size so callers can stage arbitrary payloads.
        var sized = new byte[expectedSize];
        var copy = Math.Min(data.Length, expectedSize);
        data.AsSpan(0, copy).CopyTo(sized);
        return (i.ArchiveName, sized);
      }));
  }

  /// <summary>
  /// Zeros + frees the named logical bands. Synthetic <c>band-NNNN.bin</c>
  /// names are honoured; other names are silently skipped. The physical
  /// slot is retained as a zero-filled hole so other bands' byte offsets
  /// don't shift.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    AppleSparseInPlaceModifier.RemoveBands(archive, entryNames);
  }
}
