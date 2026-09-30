#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.OneNote;

/// <summary>
/// Microsoft OneNote section (<c>.one</c> / <c>.onetoc2</c>) read-only pseudo-archive.
/// It surfaces the original file, a parsed fixed-header summary, and bounded raw copies of
/// the root file-node list and transaction-log fragment when their references validate.
/// The revision/object graph itself is not decoded, and nothing is written: a byte copy of an
/// existing file is not a OneNote writer, so creation is not offered.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://learn.microsoft.com/en-us/openspecs/office_file_formats/ms-onestore/</c> — [MS-ONESTORE] OneNote Revision Store File Format — Microsoft Open Specifications</description></item>
///   <item><description><c>https://learn.microsoft.com/en-us/openspecs/office_file_formats/ms-one/</c> — [MS-ONE] OneNote section data structures</description></item>
/// </list>
/// </summary>
public sealed class OneNoteFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "OneNote";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Microsoft OneNote";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".one";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".one", ".onetoc2"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new(OneNoteDetector.Guid2010Plus, Offset: 0, Confidence: 0.95),
    new(OneNoteDetector.GuidTableOfContents, Offset: 0, Confidence: 0.95),
    new(OneNoteDetector.Guid2007, Offset: 0, Confidence: 0.95),
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("one", "OneNote")];
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
  public string Description => "Microsoft OneNote section/TOC revision store (fixed-header and first root-fragment checks; bounded raw-region extraction)";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    ArgumentNullException.ThrowIfNull(stream);
    var fileSize = stream.Length;
    var meta = BuildMetadataIni(stream);
    var entries = new List<ArchiveEntryInfo> {
      new(0, FullEntryName(stream), fileSize, -1, "Stored", false, false, null),
    };
    if (TryGetInspectableHeader(stream, out var header)) {
      entries.Add(new(entries.Count, "root_file_node_list.bin", header.RootFileNodeListLength, -1, "Stored", false, false, null));
      entries.Add(new(entries.Count, "transaction_log_fragment.bin", header.TransactionLogLength, -1, "Stored", false, false, null));
    }
    entries.Add(new(entries.Count, "metadata.ini", meta.Length, -1, "Stored", false, false, null));
    return entries;
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(outputDir);

    var fullName = FullEntryName(stream);
    if (files == null || files.Length == 0 || MatchesFilter(fullName, files))
      WriteRange(stream, outputDir, fullName, 0, stream.Length);

    if (TryGetInspectableHeader(stream, out var header)) {
      if (files == null || files.Length == 0 || MatchesFilter("root_file_node_list.bin", files))
        WriteRange(stream, outputDir, "root_file_node_list.bin", (long)header.RootFileNodeListOffset, header.RootFileNodeListLength);
      if (files == null || files.Length == 0 || MatchesFilter("transaction_log_fragment.bin", files))
        WriteRange(stream, outputDir, "transaction_log_fragment.bin", (long)header.TransactionLogOffset, header.TransactionLogLength);
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
    if (string.Equals(entryName, FullEntryName(archive), StringComparison.OrdinalIgnoreCase)) {
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new Compression.Registry.Streaming.ReadOnlyStreamSlice(archive, 0, archive.Length),
        archive.Length, leaveOpen: false);
    }
    if (TryGetInspectableHeader(archive, out var header)) {
      var range = entryName.Equals("root_file_node_list.bin", StringComparison.OrdinalIgnoreCase)
        ? ((long)header.RootFileNodeListOffset, (long)header.RootFileNodeListLength)
        : entryName.Equals("transaction_log_fragment.bin", StringComparison.OrdinalIgnoreCase)
          ? ((long)header.TransactionLogOffset, (long)header.TransactionLogLength)
          : ((long)-1, (long)0);
      if (range.Item1 >= 0)
        return new Compression.Registry.Streaming.BoundedEntryStream(
          new Compression.Registry.Streaming.ReadOnlyStreamSlice(archive, range.Item1, range.Item2), range.Item2, leaveOpen: false);
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
    var fileType = OneNoteDetector.DetectFileType(stream);
    sb.Append("file_type = ").AppendLine(fileType switch {
      OneNoteFileType.Section => "section (.one)",
      OneNoteFileType.TableOfContents => "table of contents (.onetoc2)",
      _ => "legacy or unrecognized",
    });
    if (OneNoteHeader.TryRead(stream, out var header)) {
      sb.Append("header_status = ").AppendLine(header.HasValidReferences(fileSize) ? "validated" : "invalid references or length");
      sb.Append("guid_file = ").AppendLine(header.FileId.ToString("D"));
      sb.Append("guid_ancestor = ").AppendLine(header.AncestorId.ToString("D"));
      sb.Append("guid_file_format = ").AppendLine(header.FileFormatId.ToString("D"));
      sb.Append("file_name_crc32 = 0x").AppendLine(header.NameCrc.ToString("X8", CultureInfo.InvariantCulture));
      sb.Append("last_writer_version = ").AppendLine(header.LastWriterVersion.ToString(CultureInfo.InvariantCulture));
      sb.Append("oldest_writer_version = ").AppendLine(header.OldestWriterVersion.ToString(CultureInfo.InvariantCulture));
      sb.Append("newest_writer_version = ").AppendLine(header.NewestWriterVersion.ToString(CultureInfo.InvariantCulture));
      sb.Append("oldest_reader_version = ").AppendLine(header.OldestReaderVersion.ToString(CultureInfo.InvariantCulture));
      sb.Append("expected_file_size = ").AppendLine(header.ExpectedFileLength.ToString(CultureInfo.InvariantCulture));
      sb.Append("guid_file_version = ").AppendLine(header.FileVersionId.ToString("D"));
      sb.Append("file_version_generation = ").AppendLine(header.FileVersionGeneration.ToString(CultureInfo.InvariantCulture));
      sb.Append("root_file_node_list_offset = ").AppendLine(header.RootFileNodeListOffset.ToString(CultureInfo.InvariantCulture));
      sb.Append("root_file_node_list_length = ").AppendLine(header.RootFileNodeListLength.ToString(CultureInfo.InvariantCulture));
      sb.Append("transaction_log_offset = ").AppendLine(header.TransactionLogOffset.ToString(CultureInfo.InvariantCulture));
      sb.Append("transaction_log_length = ").AppendLine(header.TransactionLogLength.ToString(CultureInfo.InvariantCulture));
      if (header.HasValidReferences(fileSize) && OneNoteFileNodeListFragment.TryRead(stream, header.RootFileNodeListOffset, header.RootFileNodeListLength, out var fragment)) {
        sb.Append("root_file_node_list_id = ").AppendLine(fragment.FileNodeListId.ToString(CultureInfo.InvariantCulture));
        sb.Append("root_file_node_list_sequence = ").AppendLine(fragment.Sequence.ToString(CultureInfo.InvariantCulture));
      sb.Append("root_file_node_fragment_node_count = ").AppendLine(fragment.FileNodeCount.ToString(CultureInfo.InvariantCulture));
        sb.Append("root_file_node_list_has_next_fragment = ").AppendLine(fragment.HasNextFragment.ToString(CultureInfo.InvariantCulture).ToLowerInvariant());
      } else
        sb.AppendLine("root_file_node_list_status = malformed");
    }
    sb.AppendLine("parse_status = partial");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static bool TryGetInspectableHeader(Stream stream, out OneNoteHeader header)
    => OneNoteHeader.TryRead(stream, out header) && header.HasValidReferences(stream.Length);

  private static string FullEntryName(Stream stream)
    => OneNoteDetector.DetectFileType(stream) == OneNoteFileType.TableOfContents ? "FULL.onetoc2" : "FULL.one";

  private static void WriteRange(Stream source, string outputDir, string name, long offset, long length) {
    source.Position = offset;
    var path = Path.Combine(outputDir, name);
    var dir = Path.GetDirectoryName(path);
    if (dir != null) Directory.CreateDirectory(dir);
    using var target = File.Create(path);
    var remaining = length;
    var buffer = new byte[64 * 1024];
    while (remaining > 0) {
      var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
      if (read == 0) throw new EndOfStreamException("OneNote region ended before its declared length.");
      target.Write(buffer, 0, read);
      remaining -= read;
    }
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
