#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Tux2;

/// <summary>
/// Opaque/manual descriptor for Daniel Phillips's TUX2 phase-tree research filesystem.
/// </summary>
/// <remarks>
/// TUX2 was announced as an Ext2 variation and explicitly aimed to mount existing Ext2
/// partitions. No stable, independently identifying TUX2 disk format or magic was published.
/// Consequently this descriptor does not manufacture a private signature, writer, modifier or
/// defragmenter and does not claim that an Ext2 image is uniquely TUX2. Manual selection surfaces
/// the image plus compatibility metadata for forensic work.
/// </remarks>
public sealed class Tux2FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, ISyntheticEntryNames {
  private static readonly HashSet<string> SyntheticNames =
    new(StringComparer.OrdinalIgnoreCase) { "FULL.tux2", "metadata.ini" };

  /// <inheritdoc />
  public IReadOnlySet<string> SyntheticEntryNames => SyntheticNames;

  /// <inheritdoc />
  public string Id => "Tux2";

  /// <inheritdoc />
  public string DisplayName => "TUX2";

  /// <inheritdoc />
  public FormatCategory Category => FormatCategory.Archive;

  /// <inheritdoc />
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest;

  /// <inheritdoc />
  public string DefaultExtension => ".tux2";

  /// <inheritdoc />
  public IReadOnlyList<string> Extensions => [".tux2"];

  /// <inheritdoc />
  public IReadOnlyList<string> CompoundExtensions => [];

  /// <inheritdoc />
  public IReadOnlyList<MagicSignature> MagicSignatures => [];

  /// <inheritdoc />
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];

  /// <inheritdoc />
  public string? TarCompressionFormatId => null;

  /// <inheritdoc />
  public AlgorithmFamily Family => AlgorithmFamily.Archive;

  /// <inheritdoc />
  public string Description =>
    "TUX2 phase-tree research filesystem — no stable standalone on-disk signature; manual opaque image surface only.";

  /// <inheritdoc />
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var reader = new Tux2Reader(stream);
    return reader.Entries.Select((entry, index) => new ArchiveEntryInfo(
      index, entry.Name, entry.Size, entry.Size, "Stored", entry.IsDirectory, false, null)).ToList();
  }

  /// <inheritdoc />
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var reader = new Tux2Reader(stream);
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
