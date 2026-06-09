#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Wbn;

/// <summary>
/// Web Bundle / Bundled HTTP Exchanges (<c>.wbn</c>) read-only pseudo-archive. Validates
/// the CBOR-array preamble, walks just enough of the outer structure to surface the
/// version tag, primary URL, and resource count, then emits a <c>FULL.wbn</c> passthrough
/// alongside a <c>metadata.ini</c> summary. Per-resource extraction is intentionally out
/// of scope — it requires a full CBOR decoder plus HTTP request/response framing to
/// rebuild the embedded URL tree.
/// </summary>
public sealed class WbnFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {

  public string Id => "Wbn";
  public string DisplayName => "Web Bundle";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".wbn";
  public IReadOnlyList<string> Extensions => [".wbn"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new(WbnConstants.Magic, Offset: 0, Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("webbundle", "Web Bundle")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Web Bundle / Bundled HTTP Exchanges (read-only pseudo-archive)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    ArgumentNullException.ThrowIfNull(stream);
    var fileSize = stream.Length;
    var meta = BuildMetadataIni(stream);
    return [
      new ArchiveEntryInfo(0, "FULL.wbn", fileSize, -1, "Stored", false, false, null),
      new ArchiveEntryInfo(1, "metadata.ini", meta.Length, -1, "Stored", false, false, null),
    ];
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(outputDir);

    if (files == null || files.Length == 0 || MatchesFilter("FULL.wbn", files)) {
      stream.Seek(0, SeekOrigin.Begin);
      var fullPath = Path.Combine(outputDir, "FULL.wbn");
      var dir = Path.GetDirectoryName(fullPath);
      if (dir != null) Directory.CreateDirectory(dir);
      using var outStream = File.Create(fullPath);
      stream.CopyTo(outStream);
    }

    if (files == null || files.Length == 0 || MatchesFilter("metadata.ini", files))
      WriteFile(outputDir, "metadata.ini", BuildMetadataIni(stream));
  }

  /// <summary>
  /// Opens a single entry as a bounded read-only stream. Handles the
  /// synthetic <c>FULL.wbn</c> passthrough and the <c>metadata.ini</c>
  /// summary; both are wrapped in a
  /// <see cref="Compression.Registry.Streaming.BoundedEntryStream"/> sized
  /// to the entry's logical length.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    if (string.Equals(entryName, "FULL.wbn", StringComparison.OrdinalIgnoreCase)) {
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new Compression.Registry.Streaming.ReadOnlyStreamSlice(archive, 0, archive.Length),
        archive.Length, leaveOpen: false);
    }
    if (string.Equals(entryName, "metadata.ini", StringComparison.OrdinalIgnoreCase)) {
      var meta = BuildMetadataIni(archive);
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new MemoryStream(meta, writable: false), meta.Length, leaveOpen: false);
    }
    return new Compression.Registry.Streaming.BoundedEntryStream(
      new MemoryStream(System.Array.Empty<byte>(), writable: false), 0, leaveOpen: false);
  }

  /// <summary>Native in-memory single-entry extraction routed through the bounded <see cref="OpenEntry"/>.</summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var memoryStream = new MemoryStream();
    s.CopyTo(memoryStream);
    return memoryStream.ToArray();
  }

  /// <summary>
  /// WORM create — emits a Web Bundle whose <c>index</c> section contains one
  /// entry per non-directory input. See <see cref="WbnWriter"/> for the
  /// detailed wire layout.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options)
    => WbnWriter.Write(output, inputs, options);

  private static byte[] BuildMetadataIni(Stream stream) {
    var origin = stream.Position;
    bool magicOk;
    string version;
    string primaryUrl;
    int resourceCount;
    string parseStatus;
    try {
      stream.Seek(0, SeekOrigin.Begin);
      try {
        var reader = new WbnReader(stream);
        magicOk = reader.MagicOk;
        version = reader.Version;
        primaryUrl = reader.PrimaryUrl;
        resourceCount = reader.ResourceCount;
        parseStatus = reader.ParseStatus;
      } catch (InvalidDataException) {
        magicOk = false;
        version = "unknown";
        primaryUrl = "unknown";
        resourceCount = 0;
        parseStatus = "partial";
      }
    } finally {
      stream.Seek(origin, SeekOrigin.Begin);
    }

    var sb = new StringBuilder();
    sb.AppendLine("[webbundle]");
    sb.Append("magic_ok = ").AppendLine(magicOk ? "true" : "false");
    sb.Append("version = ").AppendLine(EscapeIniValue(version));
    sb.Append("primary_url = ").AppendLine(EscapeIniValue(primaryUrl));
    sb.Append("resource_count = ").AppendLine(resourceCount.ToString(CultureInfo.InvariantCulture));
    sb.Append("parse_status = ").AppendLine(parseStatus);
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static string EscapeIniValue(string s) {
    if (string.IsNullOrEmpty(s)) return string.Empty;
    var sb = new StringBuilder(s.Length);
    foreach (var c in s) {
      switch (c) {
        case '\\': sb.Append("\\\\"); break;
        case '"': sb.Append("\\\""); break;
        case '\r': sb.Append("\\r"); break;
        case '\n': sb.Append("\\n"); break;
        default: sb.Append(c); break;
      }
    }
    return sb.ToString();
  }
}
