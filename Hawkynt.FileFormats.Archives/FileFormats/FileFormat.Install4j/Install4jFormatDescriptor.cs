using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Install4j;

/// <summary>
/// install4j installers (ej-technologies): the launcher block and the data archives (ZIP, stored
/// or LZMA-compressed). Read-only; detected by the marker that starts the PE overlay.
/// </summary>
public sealed class Install4jFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  /// <inheritdoc />
  public string Id => "Install4j";
  /// <inheritdoc />
  public string DisplayName => "install4j";
  /// <inheritdoc />
  public FormatCategory Category => FormatCategory.Archive;
  /// <inheritdoc />
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.SupportsMultipleEntries;
  /// <inheritdoc />
  public string DefaultExtension => "";
  /// <inheritdoc />
  public IReadOnlyList<string> Extensions => [];
  /// <inheritdoc />
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <inheritdoc />
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  /// <inheritdoc />
  public IReadOnlyList<FormatMethodInfo> Methods => [new("store", "Stored"), new("lzma", "LZMA")];
  /// <inheritdoc />
  public string? TarCompressionFormatId => null;
  /// <inheritdoc />
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <inheritdoc />
  public string Description =>
    "install4j installer: the launcher block and the data archives (ZIP, stored or LZMA). " +
    "The files inside the archives are read through the ZIP format.";

  private static (Install4jReader? Reader, string? Problem) Open(Stream stream) {
    try {
      stream.Position = 0;
      using var copy = new MemoryStream();
      stream.CopyTo(copy);
      return (Install4jReader.Open(copy.ToArray()), null);
    } catch (Exception e) when (e is InvalidDataException or NotSupportedException or EndOfStreamException) {
      return (null, e.Message);
    }
  }

  private static byte[] Problem(string problem) => System.Text.Encoding.UTF8.GetBytes(
    "[Install4j]\nproblem=" + problem.Replace('\n', ' ') + "\nmember_enumeration=unavailable\nparse_status=partial\n");

  /// <inheritdoc />
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var (reader, problem) = Open(stream);
    if (reader == null)
      return [
        new(0, "FULL.exe", stream.Length, stream.Length, "Stored", false, false, null, Kind: "Track"),
        new(1, "metadata.ini", Problem(problem!).Length, 0, "Stored", false, false, null, Kind: "Tag"),
      ];
    return [.. reader.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Size, e.StoredSize, e.IsLzma ? "lzma" : "store", false, false, null))];
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
        WriteFile(outputDir, "metadata.ini", Problem(problem!));
      return;
    }
    foreach (var entry in reader.Entries) {
      if (files is { Length: > 0 } && !MatchesFilter(entry.Name, files))
        continue;
      WriteFile(outputDir, entry.Name, reader.Extract(entry));
    }
  }
}
