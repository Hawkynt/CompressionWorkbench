using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Paq8l;

/// <summary>
/// paq8l archive (Matt Mahoney et al., 2007): a context-mixing compressor whose output is
/// defined by its model, written from docs/PAQ8L-FORMAT.md. Archives read and written here
/// are byte-identical to what paq8l itself writes and reads.
/// </summary>
public sealed class Paq8lFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
  /// <inheritdoc />
  public string Id => "Paq8l";
  /// <inheritdoc />
  public string DisplayName => "paq8l";
  /// <inheritdoc />
  public FormatCategory Category => FormatCategory.Archive;
  /// <inheritdoc />
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest | FormatCapabilities.CanCreate |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  /// <inheritdoc />
  public string DefaultExtension => ".paq8l";
  /// <inheritdoc />
  public IReadOnlyList<string> Extensions => [".paq8l"];
  /// <inheritdoc />
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <inheritdoc />
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([.. Paq8lArchive.Signature], Confidence: 0.9)];
  /// <inheritdoc />
  public IReadOnlyList<FormatMethodInfo> Methods => [new("paq8l", "paq8l context mixing (level 0-9, default 5)")];
  /// <inheritdoc />
  public string? TarCompressionFormatId => null;
  /// <inheritdoc />
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <inheritdoc />
  public string Description =>
    "paq8l context-mixing archive; levels 0-9 (memory 65536 << level × about 3.6); byte-identical with paq8l 2007";

  /// <inheritdoc />
  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    Paq8lArchive.List(stream).Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Size, -1, "paq8l", false, false, null)).ToList();

  /// <inheritdoc />
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var (entry, data) in Paq8lArchive.Read(stream)) {
      if (files is { Length: > 0 } && !MatchesFilter(entry.Name, files)) continue;
      WriteFile(outputDir, entry.Name, data);
    }
  }

  /// <summary>
  /// Writes the inputs as one archive at <see cref="FormatCreateOptions.Level"/> (0–9, default 5).
  /// Directories are not stored: paq8l records only files, with their relative paths.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(inputs);
    var files = inputs.Where(i => !i.IsDirectory)
      .Select(i => (i.ArchiveName.Replace('\\', '/').TrimStart('/'), i.ReadContent()))
      .ToList();
    Paq8lArchive.Write(output, files, options?.Level ?? Paq8lArchive.DefaultLevel);
  }
}
