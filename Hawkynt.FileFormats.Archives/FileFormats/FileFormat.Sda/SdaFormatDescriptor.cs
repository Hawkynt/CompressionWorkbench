using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Sda;

/// <summary>
/// Self Dissolving ARChive (.sda): Chris Smeets' Commodore 64/128 ARC archive behind its 6502
/// extractor. Read-only: writing one would mean shipping that extractor, which is not ours to
/// redistribute.
/// </summary>
public sealed class SdaFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  /// <inheritdoc />
  public string Id => "Sda";
  /// <inheritdoc />
  public string DisplayName => "Self Dissolving ARChive (C64)";
  /// <inheritdoc />
  public FormatCategory Category => FormatCategory.Archive;
  /// <inheritdoc />
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  /// <inheritdoc />
  public string DefaultExtension => ".sda";
  /// <inheritdoc />
  public IReadOnlyList<string> Extensions => [".sda"];
  /// <inheritdoc />
  public IReadOnlyList<string> CompoundExtensions => [];
  // An SDA is a Commodore BASIC program file like any other; it is recognised by extension.
  /// <inheritdoc />
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  /// <inheritdoc />
  public IReadOnlyList<FormatMethodInfo> Methods => [new("sda", "C64 ARC")];
  /// <inheritdoc />
  public string? TarCompressionFormatId => null;
  /// <inheritdoc />
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <inheritdoc />
  public string Description =>
    "Commodore 64/128 self-dissolving ARC (Chris Smeets): stored, packed, squeezed, crunched and squashed entries, checksum-verified; read-only";

  private static byte[] ReadAll(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  /// <inheritdoc />
  public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => SdaArchive.List(ReadAll(stream)).Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Size, e.StoredSize, e.MethodName, false, false, e.LastModified)).ToList();

  /// <inheritdoc />
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var data = ReadAll(stream);
    foreach (var e in SdaArchive.List(data)) {
      if (files is { Length: > 0 } && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, SdaArchive.Extract(data, e));
    }
  }
}
