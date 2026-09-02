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
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://datatracker.ietf.org/doc/draft-ietf-wpack-bundled-responses/</c> — IETF WPACK "Web Bundles" (Bundled HTTP Responses) draft</description></item>
///   <item><description><c>https://github.com/WICG/webpackage</c> — WICG web packaging incubation — spec text and reference tooling</description></item>
/// </list>
/// </summary>
public sealed class WbnFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Wbn";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Web Bundle";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".wbn";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".wbn"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new(WbnConstants.Magic, Offset: 0, Confidence: 0.95),
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("webbundle", "Web Bundle")];
  /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
  public string? TarCompressionFormatId => null;
  /// <summary>
  /// Gets the family.
  /// </summary>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description =>
    "Web Bundle / Bundled HTTP Exchanges (read-only pseudo-archive). The " +
    "outer CBOR array stores a `sections-lengths` byte string at index 2 that " +
    "encodes the byte-length of every following section as a flat CBOR map " +
    "(name -> length). Any in-place mutation of a section body changes its " +
    "byte-length, which would require re-encoding `sections-lengths` (and the " +
    "subsequent section offsets in the `index` map are absolute, so they shift " +
    "too). Signed Web Bundles additionally carry an Ed25519 signature over the " +
    "manifest in the `authorities` section, so even a successful structural " +
    "edit fails verification. This descriptor therefore does not implement " +
    "IArchiveModifiable.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    ArgumentNullException.ThrowIfNull(stream);
    var fileSize = stream.Length;
    var meta = BuildMetadataIni(stream);
    return [
      new ArchiveEntryInfo(0, "FULL.wbn", fileSize, -1, "Stored", false, false, null),
      new ArchiveEntryInfo(1, "metadata.ini", meta.Length, -1, "Stored", false, false, null),
    ];
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
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
