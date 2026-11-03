using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.DxArchive;

/// <summary>
/// DxLib archives (<c>.dxa</c>; WOLF RPG Editor's <c>.wolf</c> data archives), versions 6 and 8.
/// The password is the archiver's default unless given; version 6 keys are also recovered from
/// the header, so those archives open without one.
/// </summary>
public sealed class DxArchiveFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  /// <inheritdoc />
  public string Id => "DxArchive";
  /// <inheritdoc />
  public string DisplayName => "DxLib archive (DXA)";
  /// <inheritdoc />
  public FormatCategory Category => FormatCategory.Archive;
  /// <inheritdoc />
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories | FormatCapabilities.SupportsPassword;
  /// <inheritdoc />
  public string DefaultExtension => ".dxa";
  /// <inheritdoc />
  public IReadOnlyList<string> Extensions => [".dxa", ".wolf"];
  /// <inheritdoc />
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <inheritdoc />
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x44, 0x58, 0x08, 0x00], Confidence: 0.7)];
  /// <inheritdoc />
  public IReadOnlyList<FormatMethodInfo> Methods => [new("dxa", "DxLib LZ + Huffman, XOR-keyed")];
  /// <inheritdoc />
  public string? TarCompressionFormatId => null;
  /// <inheritdoc />
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <inheritdoc />
  public string Description =>
    "DxLib game archive (.dxa, WOLF RPG .wolf), versions 6 and 8: XOR-keyed, LZ and Huffman coded. " +
    "Version 8 needs the archive password unless the default was used; version 6 keys are recovered.";

  private static (DxArchiveReader? Reader, string? Problem) Open(Stream stream, string? password) {
    try {
      stream.Position = 0;
      using var copy = new MemoryStream();
      stream.CopyTo(copy);
      return (DxArchiveReader.Open(copy.ToArray(), string.IsNullOrEmpty(password) ? null : password), null);
    } catch (Exception e) when (e is InvalidDataException or NotSupportedException or EndOfStreamException) {
      return (null, e.Message);
    }
  }

  private static byte[] Problem(string problem) => System.Text.Encoding.UTF8.GetBytes(
    "[DxArchive]\nproblem=" + problem.Replace('\n', ' ') + "\nmember_enumeration=unavailable\nparse_status=partial\n");

  /// <inheritdoc />
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var (reader, problem) = Open(stream, password);
    if (reader == null)
      return [
        new(0, "FULL.dxa", stream.Length, stream.Length, "Stored", false, false, null, Kind: "Track"),
        new(1, "metadata.ini", Problem(problem!).Length, 0, "Stored", false, false, null, Kind: "Tag"),
      ];
    return [.. reader.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Path, e.Size, e.StoredSize, $"DXA v{reader.Version}", false, e.Key != null, e.Modified))];
  }

  /// <inheritdoc />
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var (reader, problem) = Open(stream, password);
    if (reader == null) {
      if (files is not { Length: > 0 } || MatchesFilter("FULL.dxa", files)) {
        stream.Position = 0;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        WriteFile(outputDir, "FULL.dxa", ms.ToArray());
      }
      if (files is not { Length: > 0 } || MatchesFilter("metadata.ini", files))
        WriteFile(outputDir, "metadata.ini", Problem(problem!));
      return;
    }
    foreach (var entry in reader.Entries) {
      if (files is { Length: > 0 } && !MatchesFilter(entry.Path, files))
        continue;
      WriteFile(outputDir, entry.Path, reader.Extract(entry));
    }
  }
}
