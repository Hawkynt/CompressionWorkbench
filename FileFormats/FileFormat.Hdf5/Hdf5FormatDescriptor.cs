#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Hdf5;

/// <summary>
/// Read-only, metadata-surfacing descriptor for HDF5. Does not walk the full B-tree /
/// local-heap / object-header graph; only reads the superblock and does a best-effort
/// scan for object-header signatures (<c>OHDR</c>) in a bounded prefix of the payload.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/HDFGroup/hdf5</c> — canonical implementation (libhdf5); the on-disk format specification is maintained in its documentation</description></item>
///   <item><description><c>https://www.hdfgroup.org/solutions/hdf5/</c> — HDF Group HDF5 portal</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Hierarchical_Data_Format</c> — Wikipedia</description></item>
/// </list>
/// </summary>
public sealed class Hdf5FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  // HDF5 file signature: 0x89 "HDF" \r \n 0x1A \n
  internal static readonly byte[] Hdf5Signature =
    [0x89, 0x48, 0x44, 0x46, 0x0D, 0x0A, 0x1A, 0x0A];

  // Scan cap: files larger than this only have their first 64 MB examined for OHDR signatures.
  // Real HDF5 files with 100s of MB of object headers need a B-tree walker anyway.
  private const int ScanCapBytes = 64 * 1024 * 1024;

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Hdf5";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "HDF5";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".h5";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".h5", ".hdf5"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new(Hdf5Signature, Confidence: 0.95)];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
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
  public string Description => "Hierarchical Data Format v5 (metadata surfacing only)";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = new List<ArchiveEntryInfo> {
      new(0, "FULL.h5", stream.Length, stream.Length, "stored", false, false, null, "Source"),
    };
    foreach (var e in BuildSynthetic(stream))
      entries.Add(new ArchiveEntryInfo(
        entries.Count, e.Name, e.Data.LongLength, e.Data.LongLength,
        "stored", false, false, null, e.Kind));
    return entries;
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    // Stream FULL.h5 directly — never buffer the whole file.
    if (files == null || files.Length == 0 || MatchesFilter("FULL.h5", files)) {
      stream.Seek(0, SeekOrigin.Begin);
      var fullPath = Path.Combine(outputDir, "FULL.h5");
      var dir = Path.GetDirectoryName(fullPath);
      if (dir != null) Directory.CreateDirectory(dir);
      using var outStream = File.Create(fullPath);
      stream.CopyTo(outStream);
    }
    foreach (var e in BuildSynthetic(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  // Reads only a bounded prefix (up to 64 MB) of the stream for OHDR scanning.
  // Marks metadata with ohdr_scan_truncated=true when file exceeds the cap.
  private static List<(string Name, byte[] Data, string Kind)> BuildSynthetic(Stream stream) {
    stream.Seek(0, SeekOrigin.Begin);
    var streamLen = stream.Length;
    var toRead = (int)Math.Min(streamLen, ScanCapBytes);
    var scanTruncated = streamLen > ScanCapBytes;

    var prefix = new byte[toRead];
    var read = 0;
    while (read < toRead) {
      var n = stream.Read(prefix, read, toRead - read);
      if (n <= 0) break;
      read += n;
    }
    if (read < toRead) {
      // Stream reported a length larger than it delivered — shrink to what we got.
      Array.Resize(ref prefix, read);
    }

    string status = "partial";
    var super = new Hdf5SuperblockInfo();
    var discovered = new List<string>();
    try {
      super = Hdf5Parser.ReadSuperblock(prefix);
      if (super.Found) {
        status = "superblock_ok";
        discovered.AddRange(Hdf5Parser.ScanForObjectHeaders(prefix, super));
      }
    } catch {
      status = "error";
    }

    return [
      ("metadata.ini", BuildMetadata(super, status, discovered.Count, streamLen, scanTruncated), "Metadata"),
      ("objects.txt", BuildObjectsList(discovered), "Index"),
    ];
  }

  private static byte[] BuildMetadata(
    Hdf5SuperblockInfo super, string status, int objectCount,
    long fileSize, bool scanTruncated) {
    var sb = new StringBuilder();
    sb.Append("[hdf5]\r\n");
    sb.Append("parse_status=").Append(status).Append("\r\n");
    sb.Append("file_size=").Append(fileSize).Append("\r\n");
    sb.Append("superblock_version=").Append(super.Version).Append("\r\n");
    sb.Append("offset_size=").Append(super.OffsetSize).Append("\r\n");
    sb.Append("length_size=").Append(super.LengthSize).Append("\r\n");
    sb.Append("root_offset=").Append(super.RootOffset).Append("\r\n");
    sb.Append("superblock_offset=").Append(super.SuperblockOffset).Append("\r\n");
    sb.Append("object_count=").Append(objectCount).Append("\r\n");
    sb.Append("ohdr_scan_truncated=").Append(scanTruncated ? "true" : "false").Append("\r\n");
    return Encoding.ASCII.GetBytes(sb.ToString());
  }

  private static byte[] BuildObjectsList(IReadOnlyList<string> discovered) {
    var sb = new StringBuilder();
    foreach (var line in discovered)
      sb.Append(line).Append("\r\n");
    return Encoding.ASCII.GetBytes(sb.ToString());
  }
}
