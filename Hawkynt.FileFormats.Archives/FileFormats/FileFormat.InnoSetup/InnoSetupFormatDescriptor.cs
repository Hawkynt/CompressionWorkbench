#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.InnoSetup;

/// <summary>
/// Inno Setup installer: the files of its [Files] section.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://jrsoftware.org/isinfo.php</c> — Inno Setup; <c>https://github.com/jrsoftware/issrc</c> holds its sources (Shared.Struct.pas defines the records)</description></item>
///   <item><description><c>https://github.com/dscharrer/innoextract</c> — innoextract (zlib licence), the extractor this reader was converted from and is checked against</description></item>
/// </list>
/// </summary>
public sealed class InnoSetupFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "InnoSetup";
  public string DisplayName => "Inno Setup";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".exe";
  // Detected by content (FormatDetector.DetectInstaller), never by ".exe".
  public IReadOnlyList<string> Extensions => [];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods =>
    [new("stored", "Stored"), new("zlib", "zlib"), new("bzip2", "bzip2"), new("lzma", "LZMA"), new("lzma2", "LZMA2")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Inno Setup installer: the files of its [Files] section, checksum-verified";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    InnoSetupReader reader;
    try {
      reader = new InnoSetupReader(stream);
    } catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or EndOfStreamException) {
      var note = Metadata(ex.Message);
      return [new ArchiveEntryInfo(0, "metadata.ini", note.Length, note.Length, "stored", false, false, null, Kind: "metadata")];
    }
    var method = MethodName(reader.Compression);
    return reader.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FileName, e.Size, e.Size, method, false,
      reader.IsEncrypted, e.LastModified)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    InnoSetupReader reader;
    try {
      reader = new InnoSetupReader(stream);
    } catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or EndOfStreamException) {
      WriteFile(outputDir, "metadata.ini", Metadata(ex.Message));
      return;
    }
    var wanted = reader.Entries.Where(e => files == null || MatchesFilter(e.FileName, files)).ToList();
    reader.ExtractMany(wanted, (e, data) => WriteFile(outputDir, e.FileName, data));
  }

  /// <summary>Opens one entry as a bounded read-only stream.</summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    var bytes = this.ExtractEntryToMemory(archive, entryName, password);
    return new Compression.Registry.Streaming.BoundedEntryStream(new MemoryStream(bytes, writable: false), bytes.Length, leaveOpen: false);
  }

  /// <summary>Decodes one entry into memory; an unknown name yields no bytes.</summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek)
      archive.Position = 0;
    var reader = new InnoSetupReader(archive);
    var entry = reader.Entries.FirstOrDefault(e => string.Equals(e.FileName, entryName, StringComparison.OrdinalIgnoreCase));
    return entry == null ? [] : reader.Extract(entry);
  }

  private static string MethodName(InnoCompression c) => c switch {
    InnoCompression.Zlib => "zlib",
    InnoCompression.BZip2 => "bzip2",
    InnoCompression.Lzma1 => "lzma",
    InnoCompression.Lzma2 => "lzma2",
    _ => "stored",
  };

  private static byte[] Metadata(string error)
    => Encoding.UTF8.GetBytes($"format=innosetup\nparse_status=partial\nerror={error.Replace('\n', ' ')}\n");
}
