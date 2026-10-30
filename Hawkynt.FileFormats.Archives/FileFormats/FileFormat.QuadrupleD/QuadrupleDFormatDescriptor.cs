using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.QuadrupleD;

/// <summary>
/// Quadruple D (QDA0) game archive, read-only: stored or byte-pair-encoded files. No program that
/// writes the format is available to check a writer against.
/// </summary>
public sealed class QuadrupleDFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  /// <inheritdoc />
  public string Id => "QuadrupleD";
  /// <inheritdoc />
  public string DisplayName => "Quadruple D";
  /// <inheritdoc />
  public FormatCategory Category => FormatCategory.Archive;
  /// <inheritdoc />
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  /// <inheritdoc />
  public string DefaultExtension => ".qda";
  /// <inheritdoc />
  public IReadOnlyList<string> Extensions => [".qda"];
  /// <inheritdoc />
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <inheritdoc />
  public IReadOnlyList<MagicSignature> MagicSignatures => [new("QDA0"u8.ToArray(), Offset: 4, Confidence: 0.85)];
  /// <inheritdoc />
  public IReadOnlyList<FormatMethodInfo> Methods => [new("bpe", "Byte pair encoding")];
  /// <inheritdoc />
  public string? TarCompressionFormatId => null;
  /// <inheritdoc />
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <inheritdoc />
  public string Description => "Quadruple D (QDA0) game archive: stored or byte-pair-encoded files; read-only";

  private static byte[] ReadAll(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  /// <inheritdoc />
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var data = ReadAll(stream);
    var method = QuadrupleDArchive.IsQuadrupleD(data) && QuadrupleDArchive.IsCompressed(data) ? "BPE" : "Stored";
    return QuadrupleDArchive.List(data).Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Size, e.StoredSize, method, false, false, null)).ToList();
  }

  /// <inheritdoc />
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var data = ReadAll(stream);
    foreach (var e in QuadrupleDArchive.List(data)) {
      if (files is { Length: > 0 } && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, QuadrupleDArchive.Extract(data, e));
    }
  }
}
