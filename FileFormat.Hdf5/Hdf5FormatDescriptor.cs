#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Hdf5;

/// <summary>
/// Read-only, metadata-surfacing descriptor for HDF5. Does not walk the full B-tree /
/// local-heap / object-header graph; only reads the superblock and does a best-effort
/// scan for object-header signatures (<c>OHDR</c>) in a bounded prefix of the payload.
/// </summary>
public sealed class Hdf5FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  // HDF5 file signature: 0x89 "HDF" \r \n 0x1A \n
  internal static readonly byte[] Hdf5Signature =
    [0x89, 0x48, 0x44, 0x46, 0x0D, 0x0A, 0x1A, 0x0A];

  // Scan cap: files larger than this only have their first 64 MB examined for OHDR signatures.
  // Real HDF5 files with 100s of MB of object headers need a B-tree walker anyway.
  private const int ScanCapBytes = 64 * 1024 * 1024;

  public string Id => "Hdf5";
  public string DisplayName => "HDF5";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".h5";
  public IReadOnlyList<string> Extensions => [".h5", ".hdf5"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new(Hdf5Signature, Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Hierarchical Data Format v5 (metadata surfacing only)";

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
