#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Nsis;

/// <summary>
/// NSIS (Nullsoft Scriptable Install System) installer: the files its install script extracts.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/kichik/nsis</c> — NSIS sources; <c>Source/exehead/fileform.h</c> defines the layout (zlib/libpng licence)</description></item>
///   <item><description><c>https://nsis.sourceforge.io/Docs/</c> — NSIS documentation</description></item>
///   <item><description><c>https://www.7-zip.org</c> — 7-Zip's NSIS handler, the reference reader this one is checked against</description></item>
/// </list>
/// </summary>
public sealed class NsisFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {

  public string Id => "Nsis";
  public string DisplayName => "NSIS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest | FormatCapabilities.CanCreate |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".exe";
  // Detection is by the "NullsoftInst" first header (FormatDetector.DetectInstaller), not by ".exe".
  public IReadOnlyList<string> Extensions => [];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("zlib", "zlib"), new("bzip2", "bzip2"), new("lzma", "LZMA"), new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "NSIS installer (Nullsoft Scriptable Install System): the files of its install script";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    NsisReader reader;
    try {
      reader = new NsisReader(stream, leaveOpen: true);
      reader.ResolveSizes();
    } catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or EndOfStreamException) {
      var note = Metadata(null, ex.Message);
      return [new ArchiveEntryInfo(0, "metadata.ini", note.Length, note.Length, "stored", false, false, null, Kind: "metadata")];
    }

    var method = MethodName(reader);
    return reader.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FileName, e.Size, e.CompressedSize < 0 ? e.Size : e.CompressedSize,
      method, false, false, e.LastModified)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    NsisReader reader;
    try {
      reader = new NsisReader(stream, leaveOpen: true);
    } catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or EndOfStreamException) {
      WriteFile(outputDir, "metadata.ini", Metadata(null, ex.Message));
      return;
    }

    var wanted = reader.Entries.Where(e => files == null || MatchesFilter(e.FileName, files)).ToList();
    reader.ExtractMany(wanted, (e, data) => WriteFile(outputDir, e.FileName, data));
  }

  /// <summary>
  /// Writes an installer that extracts every input below the installation directory. The method
  /// is "zlib", "bzip2" or "lzma" (the default), with ":solid" for one solid stream. The exehead
  /// stub comes from <c>FormatSpecific["stub"]</c> (a stub file), <c>FormatSpecific["nsisdir"]</c>
  /// or an installed NSIS 3 (<see cref="NsisInstallerWriter.LoadStub"/>);
  /// <c>FormatSpecific["name"]</c> sets the product name.
  /// </summary>
  /// <exception cref="InvalidOperationException">No NSIS 3 stub is available.</exception>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    options ??= new FormatCreateOptions();
    var method = (options.MethodName ?? "lzma").ToLowerInvariant();
    var solid = method.EndsWith(":solid", StringComparison.Ordinal)
      || (options.FormatSpecific.TryGetValue("solid", out var s) && bool.TryParse(s, out var b) && b);
    var compression = method.Split(':')[0] switch {
      "zlib" or "deflate" => NsisCompression.Zlib,
      "bzip2" => NsisCompression.BZip2,
      "lzma" or "" => NsisCompression.Lzma,
      var other => throw new ArgumentException($"NSIS installers are compressed with zlib, bzip2 or lzma, not '{other}'."),
    };
    var stub = options.FormatSpecific.TryGetValue("stub", out var stubPath) && File.Exists(stubPath)
      ? File.ReadAllBytes(stubPath)
      : NsisInstallerWriter.LoadStub(compression, solid, options.FormatSpecific.GetValueOrDefault("nsisdir"));
    if (stub == null)
      throw new InvalidOperationException(
        $"Creating an NSIS installer needs NSIS 3's exehead stub Stubs/{NsisInstallerWriter.StubFileName(compression, solid)}: "
        + "install NSIS 3, set NSISDIR, or pass FormatSpecific[\"nsisdir\"] or [\"stub\"].");
    var files = inputs.Where(i => !i.IsDirectory)
      .Select(i => new NsisInstallerFile(i.ArchiveName, i.ReadContent(), i.Metadata?.LastWriteTimeUtc?.UtcDateTime))
      .ToList();
    NsisInstallerWriter.Write(output, stub, files, new NsisInstallerOptions {
      Compression = compression,
      Solid = solid,
      Name = options.FormatSpecific.GetValueOrDefault("name") ?? "Setup",
    });
  }

  /// <summary>Opens one entry as a bounded read-only stream.</summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    var bytes = this.ExtractEntryToMemory(archive, entryName, password);
    return new Compression.Registry.Streaming.BoundedEntryStream(new MemoryStream(bytes, writable: false), bytes.Length, leaveOpen: false);
  }

  /// <summary>Decodes one entry into memory; an unknown name yields no bytes.</summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek)
      archive.Position = 0;
    using var reader = new NsisReader(archive, leaveOpen: true);
    var entry = reader.Entries.FirstOrDefault(e => string.Equals(e.FileName, entryName, StringComparison.OrdinalIgnoreCase));
    return entry == null ? [] : reader.Extract(entry);
  }

  private static string MethodName(NsisReader reader) {
    var name = reader.Compression switch {
      NsisCompression.Zlib => "zlib",
      NsisCompression.BZip2 => "bzip2",
      NsisCompression.Lzma => "lzma",
      _ => "stored",
    };
    return reader.IsSolid ? name + ":solid" : name;
  }

  private static byte[] Metadata(NsisReader? reader, string? error) {
    var sb = new StringBuilder();
    sb.Append("format=nsis\n");
    sb.Append(CultureInfo.InvariantCulture, $"parse_status={(reader == null ? "partial" : "ok")}\n");
    if (error != null)
      sb.Append("error=").Append(error.Replace('\n', ' ')).Append('\n');
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
