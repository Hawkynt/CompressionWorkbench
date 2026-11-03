using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.SetupFactory;

/// <summary>
/// Setup Factory installers (Indigo Rose, versions 5 to 9): the runtime, the installer's own
/// files and the installed files. Read-only; detected by the signature after the stub.
/// </summary>
public sealed class SetupFactoryFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  /// <inheritdoc />
  public string Id => "SetupFactory";
  /// <inheritdoc />
  public string DisplayName => "Setup Factory";
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
  public IReadOnlyList<FormatMethodInfo> Methods => [new("store", "Stored"), new("pkware", "PKWARE DCL"), new("lzma", "LZMA"), new("lzma2", "LZMA2")];
  /// <inheritdoc />
  public string? TarCompressionFormatId => null;
  /// <inheritdoc />
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <inheritdoc />
  public string Description =>
    "Setup Factory installer (versions 5 to 9): runtime, the installer's own files and the installed files " +
    "(stored, PKWARE DCL, LZMA or LZMA2), checked against their recorded CRC-32.";

  private static (SetupFactoryReader? Reader, string? Problem) Open(Stream stream) {
    try {
      stream.Position = 0;
      using var copy = new MemoryStream();
      stream.CopyTo(copy);
      return (SetupFactoryReader.Open(copy.ToArray()), null);
    } catch (Exception e) when (e is InvalidDataException or NotSupportedException or EndOfStreamException) {
      return (null, e.Message);
    }
  }

  private static byte[] Problem(string problem) => System.Text.Encoding.UTF8.GetBytes(
    "[SetupFactory]\nproblem=" + problem.Replace('\n', ' ') + "\nmember_enumeration=unavailable\nparse_status=partial\n");

  private static string MethodName(SetupFactoryCompression c) => c switch {
    SetupFactoryCompression.Pkware => "pkware",
    SetupFactoryCompression.Lzma => "lzma",
    SetupFactoryCompression.Lzma2 => "lzma2",
    _ => "store",
  };

  /// <inheritdoc />
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var (reader, problem) = Open(stream);
    if (reader == null)
      return [
        new(0, "FULL.exe", stream.Length, stream.Length, "Stored", false, false, null, Kind: "Track"),
        new(1, "metadata.ini", Problem(problem!).Length, 0, "Stored", false, false, null, Kind: "Tag"),
      ];
    return [.. reader.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Path, e.Size, e.StoredSize, MethodName(e.Compression), false, false, null))];
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
      if (files is { Length: > 0 } && !MatchesFilter(entry.Path, files))
        continue;
      WriteFile(outputDir, entry.Path, reader.Extract(entry));
    }
  }
}
