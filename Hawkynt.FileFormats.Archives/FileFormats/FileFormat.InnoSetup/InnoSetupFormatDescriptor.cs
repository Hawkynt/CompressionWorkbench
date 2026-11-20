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
public sealed class InnoSetupFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
  public string Id => "InnoSetup";
  public string DisplayName => "Inno Setup";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest | FormatCapabilities.CanCreate |
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
  public string Description => "Inno Setup installer: the files of its [Files] section, checksum-verified; " +
    "written on the stubs of an installed Inno Setup 6.0 to 7.1.1, in that release's setup data layout";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    InnoSetupReader reader;
    try {
      reader = new InnoSetupReader(stream, password);
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
      reader = new InnoSetupReader(stream, password);
    } catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or EndOfStreamException) {
      WriteFile(outputDir, "metadata.ini", Metadata(ex.Message));
      return;
    }
    var wanted = reader.Entries.Where(e => files == null || MatchesFilter(e.FileName, files)).ToList();
    reader.ExtractMany(wanted, (e, data) => WriteFile(outputDir, e.FileName, data));
  }

  /// <summary>
  /// Writes an Inno Setup installer that installs every input below its installation directory.
  /// The method is "lzma2" (the default) or "stored". The loader, setup program, messages and
  /// wizard bitmaps come from an installed Inno Setup 6.0 to 7.1.1, whose setup data layout the installer
  /// then uses: <c>FormatSpecific["innodir"]</c>, the
  /// INNOSETUPDIR environment variable, or "Inno Setup 7" or "Inno Setup 6" under Program Files
  /// (<see cref="InnoSetupStubs.Load"/>); <c>FormatSpecific["name"]</c> sets the application name.
  /// </summary>
  /// <exception cref="InvalidOperationException">No Inno Setup 6.0 to 7.1.1 installation is available.</exception>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    options ??= new FormatCreateOptions();
    if (!string.IsNullOrEmpty(options.Password))
      throw new NotSupportedException("Inno Setup encryption is not written.");
    var compression = (options.MethodName ?? "lzma2").ToLowerInvariant() switch {
      "lzma2" or "" => InnoSetupWriterCompression.Lzma2,
      "stored" or "store" => InnoSetupWriterCompression.Stored,
      var other => throw new NotSupportedException($"Inno Setup installers are written stored or LZMA2-compressed, not '{other}'."),
    };
    var stubs = InnoSetupStubs.Load(options.FormatSpecific.GetValueOrDefault("innodir"))
      ?? throw new InvalidOperationException("Creating an Inno Setup installer needs an installed Inno Setup 6.0 to 7.1.1 (SetupLdr.e32, " +
        "Setup.e32, Default.isl): install it, set INNOSETUPDIR, or pass FormatSpecific[\"innodir\"].");
    var files = inputs.Select(i => new InnoSetupInstallerFile(i.ArchiveName, i.IsDirectory ? null : i.ReadContent(),
      i.Metadata?.LastWriteTimeUtc?.UtcDateTime)).ToList();
    InnoSetupWriter.Write(output, stubs, files, new InnoSetupInstallerOptions {
      AppName = options.FormatSpecific.GetValueOrDefault("name") ?? "Setup",
      Compression = compression,
    });
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
    var reader = new InnoSetupReader(archive, password);
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
