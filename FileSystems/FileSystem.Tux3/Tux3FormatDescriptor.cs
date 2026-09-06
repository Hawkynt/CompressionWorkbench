#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Tux3;

/// <summary>
/// Read-only native-superblock descriptor for the linux-tux3 research filesystem.
/// </summary>
/// <remarks>
/// The descriptor recognises real linux-tux3 disk-format revisions and parses the packed,
/// big-endian <c>struct disksuper</c> at byte 4096. Native tree traversal and mutation are not
/// implemented, so Create/Modify/Defragment capabilities are intentionally withheld rather than
/// routing files through a private side-table that no TUX3 implementation understands.
/// </remarks>
public sealed class Tux3FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, ISyntheticEntryNames {
  private static readonly HashSet<string> SyntheticNames =
    new(StringComparer.OrdinalIgnoreCase) { "FULL.tux3", "metadata.ini", "superblock.bin" };

  /// <inheritdoc />
  public IReadOnlySet<string> SyntheticEntryNames => SyntheticNames;

  /// <inheritdoc />
  public string Id => "Tux3";

  /// <inheritdoc />
  public string DisplayName => "TUX3";

  /// <inheritdoc />
  public FormatCategory Category => FormatCategory.Archive;

  /// <inheritdoc />
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest;

  /// <inheritdoc />
  public string DefaultExtension => ".tux3";

  /// <inheritdoc />
  public IReadOnlyList<string> Extensions => [".tux3"];

  /// <inheritdoc />
  public IReadOnlyList<string> CompoundExtensions => [];

  /// <inheritdoc />
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new(Tux3Reader.Magic, Offset: Tux3Reader.SuperblockOffset, Confidence: 0.99),
    new(Tux3Reader.Legacy2012Magic, Offset: Tux3Reader.SuperblockOffset, Confidence: 0.95),
  ];

  /// <inheritdoc />
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];

  /// <inheritdoc />
  public string? TarCompressionFormatId => null;

  /// <inheritdoc />
  public AlgorithmFamily Family => AlgorithmFamily.Archive;

  /// <inheritdoc />
  public string Description =>
    "TUX3 version-tree research filesystem — native big-endian superblock detection/metadata; tree traversal and writing not yet implemented.";

  /// <inheritdoc />
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var reader = new Tux3Reader(stream);
    return reader.Entries.Select((entry, index) => new ArchiveEntryInfo(
      index, entry.Name, entry.Size, entry.Size, "Stored", entry.IsDirectory, false, null)).ToList();
  }

  /// <inheritdoc />
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var reader = new Tux3Reader(stream);
    foreach (var entry in reader.Entries) {
      if (entry.IsDirectory) continue;
      if (files is { Length: > 0 } && !MatchesFilter(entry.Name, files)) continue;

      var target = Path.Combine(outputDir, entry.Name.Replace('/', Path.DirectorySeparatorChar));
      Directory.CreateDirectory(Path.GetDirectoryName(target) ?? outputDir);
      using var output = File.Create(target);
      reader.ExtractTo(entry, output);
    }
  }
}
