using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Kgb;

/// <summary>
/// KGB Archiver 1.0 (Tomasz Pawlak, 2005-2006): PAQ6 context mixing over every file of the
/// archive as one stream, at memory levels 0 to 9.
/// </summary>
/// <remarks>
/// Written from docs/KGB-ON-DISK.md; Debian's <c>kgb</c> 1.0b4+ds extracts what this writes and
/// this extracts what it writes, at every level the tests cover.
/// </remarks>
public sealed class KgbFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable {
  /// <inheritdoc />
  public string Id => "Kgb";
  /// <inheritdoc />
  public string DisplayName => "KGB Archiver";
  /// <inheritdoc />
  public FormatCategory Category => FormatCategory.Archive;
  /// <inheritdoc />
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  /// <inheritdoc />
  public string DefaultExtension => ".kgb";
  /// <inheritdoc />
  public IReadOnlyList<string> Extensions => [".kgb"];
  /// <inheritdoc />
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <inheritdoc />
  public IReadOnlyList<MagicSignature> MagicSignatures => [new("KGB_arch -"u8.ToArray(), Confidence: 0.9)];
  /// <inheritdoc />
  public IReadOnlyList<FormatMethodInfo> Methods => [new("paq6", "PAQ6 context mixing")];
  /// <inheritdoc />
  public string? TarCompressionFormatId => null;
  /// <inheritdoc />
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <inheritdoc />
  public string Description =>
    "KGB Archiver 1.0 archive: a text header of names and sizes, then all files PAQ6-coded as one stream. " +
    "Create option level 0-9 picks the memory level (default 3, as kgb); edits re-code at the archive's own level. " +
    "The format stores no checksum.";

  /// <inheritdoc />
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var archive = KgbArchive.Read(stream);
    return [.. archive.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Size, -1, $"PAQ6 -{archive.Level}", false, false, null))];
  }

  /// <inheritdoc />
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var archive = KgbArchive.Read(stream);
    archive.Decode(stream, e => files is not { Length: > 0 } || MatchesFilter(e.Name, files), (e, data) => WriteFile(outputDir, e.Name, data));
  }

  /// <inheritdoc />
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    var kgb = KgbArchive.Read(archive);
    var index = -1;
    for (var i = 0; i < kgb.Entries.Count; ++i)
      if (string.Equals(kgb.Entries[i].Name, entryName, StringComparison.Ordinal))
        index = i;
    if (index < 0)
      return [];
    byte[] result = [];
    var target = kgb.Entries[index];
    kgb.Decode(archive, e => ReferenceEquals(e, target), (_, data) => result = data);
    return result;
  }

  /// <inheritdoc />
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);
    if (!string.IsNullOrEmpty(options.Password))
      throw new NotSupportedException("KGB Archiver 1.0 archives have no encryption.");
    var level = options.Level ?? KgbArchive.DefaultLevel;
    if (options.TryGetInt("level", out var explicitLevel))
      level = explicitLevel;
    if (level is < 0 or > 9)
      throw new ArgumentOutOfRangeException(nameof(options), "KGB memory levels are 0 to 9.");
    KgbArchive.Write(output, level, FilesOnly(inputs));
  }

  /// <inheritdoc />
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(inputs);
    Rewrite(archive, files => {
      foreach (var (name, data) in FilesOnly(inputs)) {
        var existing = files.FindIndex(f => string.Equals(f.Name, name, StringComparison.Ordinal));
        if (existing >= 0)
          files[existing] = (name, data);
        else
          files.Add((name, data));
      }
    });
  }

  /// <inheritdoc />
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(entryNames);
    var drop = new HashSet<string>(entryNames, StringComparer.Ordinal);
    Rewrite(archive, files => files.RemoveAll(f => drop.Contains(f.Name)));
  }

  /// <summary>Decodes every file, edits the list, and re-codes it at the archive's own level; the input is untouched until the new archive is complete.</summary>
  private static void Rewrite(Stream archive, Action<List<(string Name, byte[] Data)>> edit) {
    ArgumentNullException.ThrowIfNull(archive);
    var kgb = KgbArchive.Read(archive);
    var files = new List<(string Name, byte[] Data)>(kgb.Entries.Count);
    kgb.Decode(archive, _ => true, (e, data) => files.Add((e.Name, data)));
    edit(files);
    using var rebuilt = new MemoryStream();
    KgbArchive.Write(rebuilt, kgb.Level, files);
    archive.Position = 0;
    archive.SetLength(0);
    rebuilt.Position = 0;
    rebuilt.CopyTo(archive);
    archive.Flush();
  }
}
