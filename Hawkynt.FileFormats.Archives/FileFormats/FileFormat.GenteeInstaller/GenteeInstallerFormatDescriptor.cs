using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.GenteeInstaller;

/// <summary>
/// Installers made with Gentee's launcher (CreateInstall): the runtime, the script, the files the
/// setup unpacks for itself and the files it installs. Read-only; detected by the launcher header.
/// </summary>
public sealed class GenteeInstallerFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  /// <inheritdoc />
  public string Id => "GenteeInstaller";
  /// <inheritdoc />
  public string DisplayName => "Gentee installer (CreateInstall)";
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
  public IReadOnlyList<FormatMethodInfo> Methods => [new("store", "Stored"), new("lzge", "LZGE"), new("ppmd", "PPMd")];
  /// <inheritdoc />
  public string? TarCompressionFormatId => null;
  /// <inheritdoc />
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <inheritdoc />
  public string Description =>
    "Gentee launcher installer (CreateInstall): runtime, script and the GEA archives' files (stored, LZGE or PPMd), " +
    "each checked against its CRC-32. Multi-volume archives and password-protected files are not read.";

  private static (GenteeInstallerReader? Reader, string? Problem) Open(Stream stream) {
    try {
      stream.Position = 0;
      using var copy = new MemoryStream();
      stream.CopyTo(copy);
      return (GenteeInstallerReader.Open(copy.ToArray()), null);
    } catch (Exception e) when (e is InvalidDataException or NotSupportedException or EndOfStreamException) {
      return (null, e.Message);
    }
  }

  private static byte[] Problem(string problem) => System.Text.Encoding.UTF8.GetBytes(
    "[GenteeInstaller]\nproblem=" + problem.Replace('\n', ' ') + "\nmember_enumeration=unavailable\nparse_status=partial\n");

  /// <inheritdoc />
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var (reader, problem) = Open(stream);
    if (reader == null)
      return [
        new(0, "FULL.exe", stream.Length, stream.Length, "Stored", false, false, null, Kind: "Track"),
        new(1, "metadata.ini", Problem(problem!).Length, 0, "Stored", false, false, null, Kind: "Tag"),
      ];
    return [.. reader.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Path, e.Size, e.CompressedSize, "gea", false, false, e.Modified))];
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
    foreach (var (entry, data) in reader.ExtractAll()) {
      if (files is { Length: > 0 } && !MatchesFilter(entry.Path, files))
        continue;
      WriteFile(outputDir, entry.Path, data);
    }
  }
}
