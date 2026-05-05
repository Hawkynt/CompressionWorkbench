#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Ubifs;

/// <summary>
/// Read-only descriptor for UBIFS (Unsorted Block Image File System) images.
/// Surfaces triage artifacts only: passthrough, node-counts metadata,
/// plus flat inode and dentry tables. Full LPT/TNC walking is out of scope.
/// </summary>
public sealed class UbifsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Ubifs";
  public string DisplayName => "UBIFS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".ubifs";
  public IReadOnlyList<string> Extensions => [".ubifs", ".ubi", ".img"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // 0x06101831 LE = 31 18 10 06
    new([0x31, 0x18, 0x10, 0x06], Offset: 0, Confidence: 0.35),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Unsorted Block Image File System (Linux raw-flash) — triage only.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = new List<ArchiveEntryInfo>();
    byte[] image;
    try {
      image = ReadAll(stream);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.ubifs", 0, 0, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    entries.Add(new ArchiveEntryInfo(0, "FULL.ubifs", image.LongLength, image.LongLength, "stored", false, false, null));
    entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));

    var scan = UbifsScanner.Scan(image);
    if (scan.Inodes.Count > 0)
      entries.Add(new ArchiveEntryInfo(entries.Count, "inodes.txt", 0, 0, "stored", false, false, null));
    if (scan.Dentries.Count > 0)
      entries.Add(new ArchiveEntryInfo(entries.Count, "dentries.txt", 0, 0, "stored", false, false, null));
    return entries;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    byte[] image;
    try {
      image = ReadAll(stream);
    } catch {
      WriteFile(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"));
      return;
    }

    WriteIfMatch(outputDir, "FULL.ubifs", image, files);

    UbifsScanner.ScanResult scan;
    try {
      scan = UbifsScanner.Scan(image);
    } catch {
      WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"), files);
      return;
    }

    WriteIfMatch(outputDir, "metadata.ini", BuildMetadata(scan), files);

    if (scan.Inodes.Count > 0)
      WriteIfMatch(outputDir, "inodes.txt", BuildInodesTable(scan), files);
    if (scan.Dentries.Count > 0)
      WriteIfMatch(outputDir, "dentries.txt", BuildDentriesTable(scan), files);
  }

  private static void WriteIfMatch(string outputDir, string name, byte[] data, string[]? filter) {
    if (filter != null && filter.Length > 0 && !MatchesFilter(name, filter)) return;
    WriteFile(outputDir, name, data);
  }

  private static byte[] BuildMetadata(UbifsScanner.ScanResult scan) {
    var sb = new StringBuilder();
    sb.Append(CultureInfo.InvariantCulture, $"parse_status={(scan.ParseOk ? "ok" : "partial")}\n");
    sb.Append(CultureInfo.InvariantCulture, $"total_nodes={scan.TotalNodes}\n");
    sb.Append(CultureInfo.InvariantCulture, $"superblock_found={scan.SuperblockFound}\n");
    sb.Append(CultureInfo.InvariantCulture, $"leb_size_if_known={scan.LebSizeIfKnown}\n");
    sb.Append("[node_counts_by_type]\n");
    foreach (var kv in scan.NodeCountsByType.OrderBy(p => p.Key))
      sb.Append(CultureInfo.InvariantCulture, $"{NodeTypeName(kv.Key)}={kv.Value}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static byte[] BuildInodesTable(UbifsScanner.ScanResult scan) {
    var sb = new StringBuilder();
    foreach (var i in scan.Inodes)
      sb.Append(CultureInfo.InvariantCulture, $"{i.InodeNum}\t{i.Size}\t{i.Flags}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static byte[] BuildDentriesTable(UbifsScanner.ScanResult scan) {
    var sb = new StringBuilder();
    foreach (var d in scan.Dentries)
      sb.Append(CultureInfo.InvariantCulture, $"{d.ParentInode}\t{d.Name}\t{d.Type}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static string NodeTypeName(byte t) => t switch {
    0 => "inode",
    1 => "data",
    2 => "dentry",
    3 => "xentry",
    4 => "trun",
    5 => "pad",
    6 => "sb",
    7 => "master",
    8 => "ref",
    9 => "idx",
    10 => "cs",
    11 => "orph",
    _ => $"type_{t}",
  };

  private static byte[] ReadAll(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }
}
