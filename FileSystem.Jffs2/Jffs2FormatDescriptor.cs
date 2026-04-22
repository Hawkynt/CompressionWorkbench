#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Jffs2;

/// <summary>
/// Read-only descriptor for JFFS2 (Journaling Flash File System v2) images.
/// Surfaces per-node triage only: node-type counts, dirent table, inode table,
/// plus a passthrough of the original image. Inode reassembly is out of scope.
/// </summary>
public sealed class Jffs2FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Jffs2";
  public string DisplayName => "JFFS2";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".jffs2";
  public IReadOnlyList<string> Extensions => [".jffs2", ".jffs", ".img"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // 0x1985 LE = 85 19 at start of an erase block
    new([0x85, 0x19], Offset: 0, Confidence: 0.35),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Journaling Flash File System v2 — node-level triage only.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = new List<ArchiveEntryInfo>();
    byte[] image;
    try {
      image = ReadAll(stream);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.jffs2", 0, 0, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    entries.Add(new ArchiveEntryInfo(0, "FULL.jffs2", image.LongLength, image.LongLength, "stored", false, false, null));
    entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));

    Jffs2Scanner.ScanResult scan;
    try { scan = Jffs2Scanner.Scan(image); } catch { return entries; }

    if (scan.Dirents.Count > 0)
      entries.Add(new ArchiveEntryInfo(entries.Count, "dirents.txt", 0, 0, "stored", false, false, null));
    if (scan.Inodes.Count > 0)
      entries.Add(new ArchiveEntryInfo(entries.Count, "inodes.txt", 0, 0, "stored", false, false, null));
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

    WriteIfMatch(outputDir, "FULL.jffs2", image, files);

    Jffs2Scanner.ScanResult scan;
    try {
      scan = Jffs2Scanner.Scan(image);
    } catch {
      WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"), files);
      return;
    }

    WriteIfMatch(outputDir, "metadata.ini", BuildMetadata(scan), files);
    if (scan.Dirents.Count > 0)
      WriteIfMatch(outputDir, "dirents.txt", BuildDirents(scan), files);
    if (scan.Inodes.Count > 0)
      WriteIfMatch(outputDir, "inodes.txt", BuildInodes(scan), files);
  }

  private static void WriteIfMatch(string outputDir, string name, byte[] data, string[]? filter) {
    if (filter != null && filter.Length > 0 && !MatchesFilter(name, filter)) return;
    WriteFile(outputDir, name, data);
  }

  private static byte[] BuildMetadata(Jffs2Scanner.ScanResult scan) {
    var sb = new StringBuilder();
    sb.Append(CultureInfo.InvariantCulture, $"parse_status={(scan.ParseOk ? "ok" : "partial")}\n");
    sb.Append(CultureInfo.InvariantCulture, $"total_nodes={scan.TotalNodes}\n");
    sb.Append(CultureInfo.InvariantCulture, $"dirent_count={scan.DirentCount}\n");
    sb.Append(CultureInfo.InvariantCulture, $"inode_count={scan.InodeCount}\n");
    sb.Append(CultureInfo.InvariantCulture, $"cleanmarker_count={scan.CleanmarkerCount}\n");
    sb.Append(CultureInfo.InvariantCulture, $"padding_count={scan.PaddingCount}\n");
    sb.Append(CultureInfo.InvariantCulture, $"summary_count={scan.SummaryCount}\n");
    sb.Append(CultureInfo.InvariantCulture, $"erasesize_if_detectable={scan.EraseSizeIfDetectable}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static byte[] BuildDirents(Jffs2Scanner.ScanResult scan) {
    var sb = new StringBuilder();
    foreach (var d in scan.Dirents)
      sb.Append(CultureInfo.InvariantCulture, $"{d.ParentInode}\t{d.Inode}\t{d.Name}\t{d.Type}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static byte[] BuildInodes(Jffs2Scanner.ScanResult scan) {
    var sb = new StringBuilder();
    foreach (var i in scan.Inodes)
      sb.Append(CultureInfo.InvariantCulture, $"{i.Inode}\t{i.Version}\t{i.Uid}\t{i.Gid}\t{i.Mode}\t{i.Size}\t{i.Mtime}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static byte[] ReadAll(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }
}
