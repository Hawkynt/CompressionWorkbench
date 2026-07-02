#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.OneNote;

/// <summary>
/// Microsoft OneNote section (<c>.one</c> / <c>.onetoc2</c>) read-only pseudo-archive.
/// Detection only — surfaces a <c>FULL.one</c> passthrough plus a <c>metadata.ini</c>
/// summary identifying the variant (2007 vs 2010+). MS-ONESTORE revision-based packed
/// object streams are not decoded.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://learn.microsoft.com/en-us/openspecs/office_file_formats/ms-onestore/</c> — [MS-ONESTORE] OneNote Revision Store File Format — Microsoft Open Specifications</description></item>
///   <item><description><c>https://learn.microsoft.com/en-us/openspecs/office_file_formats/ms-one/</c> — [MS-ONE] OneNote section data structures</description></item>
/// </list>
/// </summary>
public sealed class OneNoteFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "OneNote";
  public string DisplayName => "Microsoft OneNote";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".one";
  public IReadOnlyList<string> Extensions => [".one", ".onetoc2"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new(OneNoteDetector.Guid2010Plus, Offset: 0, Confidence: 0.95),
    new(OneNoteDetector.Guid2007, Offset: 0, Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("one", "OneNote")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Microsoft OneNote section (read-only pseudo-archive)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    ArgumentNullException.ThrowIfNull(stream);
    var fileSize = stream.Length;
    var meta = BuildMetadataIni(stream);
    return [
      new ArchiveEntryInfo(0, "FULL.one", fileSize, -1, "Stored", false, false, null),
      new ArchiveEntryInfo(1, "metadata.ini", meta.Length, -1, "Stored", false, false, null),
    ];
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(outputDir);

    if (files == null || files.Length == 0 || MatchesFilter("FULL.one", files)) {
      stream.Seek(0, SeekOrigin.Begin);
      var fullPath = Path.Combine(outputDir, "FULL.one");
      var dir = Path.GetDirectoryName(fullPath);
      if (dir != null) Directory.CreateDirectory(dir);
      using var outStream = File.Create(fullPath);
      stream.CopyTo(outStream);
    }

    if (files == null || files.Length == 0 || MatchesFilter("metadata.ini", files))
      WriteFile(outputDir, "metadata.ini", BuildMetadataIni(stream));
  }

  /// <summary>
  /// Opens a single entry as a bounded read-only stream. The synthetic
  /// <c>FULL.one</c> entry is exposed as a passthrough slice over the whole
  /// archive; <c>metadata.ini</c> is built on the fly. Both are wrapped in
  /// <see cref="Compression.Registry.Streaming.BoundedEntryStream"/> sized
  /// to their logical length.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (string.Equals(entryName, "FULL.one", StringComparison.OrdinalIgnoreCase)) {
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

  private static byte[] BuildMetadataIni(Stream stream) {
    var origin = stream.Position;
    var fileSize = stream.Length;
    OneNoteVariant variant;
    byte[] guidBytes;
    try {
      stream.Seek(0, SeekOrigin.Begin);
      var head = new byte[16];
      var read = 0;
      while (read < 16) {
        var n = stream.Read(head, read, 16 - read);
        if (n <= 0) break;
        read += n;
      }
      guidBytes = read == 16 ? head : [];
      variant = OneNoteDetector.Detect(stream);
    } finally {
      stream.Seek(origin, SeekOrigin.Begin);
    }

    var sb = new StringBuilder();
    sb.AppendLine("[onenote]");
    sb.Append("magic_guid = ").AppendLine(FormatHexBytes(guidBytes));
    sb.Append("variant = ").AppendLine(VariantName(variant));
    sb.Append("file_size = ").AppendLine(fileSize.ToString(CultureInfo.InvariantCulture));
    sb.AppendLine("parse_status = partial");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static string FormatHexBytes(byte[] bytes) {
    if (bytes.Length == 0) return "(unavailable)";
    var sb = new StringBuilder(bytes.Length * 3);
    for (var i = 0; i < bytes.Length; i++) {
      if (i > 0) sb.Append(' ');
      sb.Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));
    }
    return sb.ToString();
  }

  private static string VariantName(OneNoteVariant v) => v switch {
    OneNoteVariant.OneNote2010Plus => "OneNote 2010+",
    OneNoteVariant.OneNote2007 => "OneNote 2007",
    _ => "Unknown",
  };
}
