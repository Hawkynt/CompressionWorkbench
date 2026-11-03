using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.WiseInstaller;

/// <summary>
/// Wise Installation System setup executables (versions 5 to 9): the installed files, the
/// installer's own support files and its script. Read-only; detected by the overlay header.
/// </summary>
public sealed class WiseInstallerFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  /// <inheritdoc />
  public string Id => "WiseInstaller";
  /// <inheritdoc />
  public string DisplayName => "Wise Installer";
  /// <inheritdoc />
  public FormatCategory Category => FormatCategory.Archive;
  /// <inheritdoc />
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  /// <inheritdoc />
  public string DefaultExtension => "";
  /// <inheritdoc />
  public IReadOnlyList<string> Extensions => [];
  /// <inheritdoc />
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <inheritdoc />
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  /// <inheritdoc />
  public IReadOnlyList<FormatMethodInfo> Methods => [new("deflate", "Deflate")];
  /// <inheritdoc />
  public string? TarCompressionFormatId => null;
  /// <inheritdoc />
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <inheritdoc />
  public string Description =>
    "Wise Installation System setup (versions 5 to 9): installed files, support files and script, " +
    "each checked against its CRC-32. Multi-volume data and PKZIP-wrapped streams are not read.";

  private static (WiseReader? Reader, string? Problem) Open(Stream stream) {
    try {
      stream.Position = 0;
      using var copy = new MemoryStream();
      stream.CopyTo(copy);
      return (WiseReader.Open(copy.ToArray()), null);
    } catch (Exception e) when (e is InvalidDataException or NotSupportedException or EndOfStreamException) {
      return (null, e.Message);
    }
  }

  private static byte[] Metadata(string problem, bool partial) => System.Text.Encoding.UTF8.GetBytes(
    "[WiseInstaller]\nproblem=" + problem.Replace('\n', ' ') +
    (partial ? "\nmember_enumeration=unavailable\nparse_status=partial\n" : "\n"));

  private static byte[]? VolumeNote(WiseReader reader) => reader.UnavailableRecords == 0 ? null : Metadata(
    $"{reader.UnavailableRecords} installed file(s) lie in later volumes of the installer, which are not read", partial: false);

  /// <inheritdoc />
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var (reader, problem) = Open(stream);
    if (reader == null)
      return [
        new(0, "FULL.exe", stream.Length, stream.Length, "Stored", false, false, null, Kind: "Track"),
        new(1, "metadata.ini", Metadata(problem!, partial: true).Length, 0, "Stored", false, false, null, Kind: "Tag"),
      ];
    var list = reader.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Path, e.Size, e.StoredSize, "deflate", false, false, e.Modified)).ToList();
    if (VolumeNote(reader) is { } note)
      list.Add(new(list.Count, "metadata.ini", note.Length, 0, "Stored", false, false, null, Kind: "Tag"));
    return list;
  }

  /// <inheritdoc />
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var (reader, problem) = Open(stream);
    if (reader == null) {
      if (files is not { Length: > 0 } || MatchesFilter("FULL.exe", files)) {
        stream.Position = 0;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        WriteFile(outputDir, "FULL.exe", ms.ToArray());
      }
      if (files is not { Length: > 0 } || MatchesFilter("metadata.ini", files))
        WriteFile(outputDir, "metadata.ini", Metadata(problem!, partial: true));
      return;
    }
    foreach (var entry in reader.Entries) {
      if (files is { Length: > 0 } && !MatchesFilter(entry.Path, files))
        continue;
      WriteFile(outputDir, entry.Path, reader.Extract(entry));
    }
    if (VolumeNote(reader) is { } note && (files is not { Length: > 0 } || MatchesFilter("metadata.ini", files)))
      WriteFile(outputDir, "metadata.ini", note);
  }
}
