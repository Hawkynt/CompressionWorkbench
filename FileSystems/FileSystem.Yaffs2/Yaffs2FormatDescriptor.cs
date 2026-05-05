#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Yaffs2;

/// <summary>
/// Read-only descriptor for YAFFS2 raw-NAND images. Auto-detects chunk/spare
/// layout, surfaces an object table and reconstructed file tree. No magic bytes
/// exist in the format itself, so detection is extension- and heuristic-based.
/// </summary>
public sealed class Yaffs2FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Yaffs2";
  public string DisplayName => "YAFFS2";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".yaffs2";
  public IReadOnlyList<string> Extensions => [".yaffs2", ".yaffs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // No true magic. Detection is primarily by extension, so no signatures registered.
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Yet Another Flash File System v2 (raw NAND image) — triage + file reconstruction.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = new List<ArchiveEntryInfo>();
    byte[] image;
    try {
      image = ReadAll(stream);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.yaffs2", 0, 0, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    entries.Add(new ArchiveEntryInfo(0, "FULL.yaffs2", image.LongLength, image.LongLength, "stored", false, false, null));
    entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));

    Yaffs2Scanner.ScanResult scan;
    try {
      scan = Yaffs2Scanner.Scan(image);
    } catch {
      return entries;
    }

    if (scan.Objects.Count > 0)
      entries.Add(new ArchiveEntryInfo(entries.Count, "directory_tree.txt", 0, 0, "stored", false, false, null));

    // Reconstruct paths for files we have data for.
    var paths = BuildPaths(scan);
    foreach (var obj in scan.Objects) {
      if (obj.Type != Yaffs2Scanner.YObjectType.File) continue;
      if (!scan.DataChunks.TryGetValue(obj.ObjectId, out var chunks) || chunks.Count == 0) continue;
      var path = paths.TryGetValue(obj.ObjectId, out var p) ? p : obj.Name;
      if (string.IsNullOrEmpty(path)) continue;
      var size = chunks.Sum(c => (long)c.Length);
      entries.Add(new ArchiveEntryInfo(entries.Count, "files/" + path, size, size, "stored", false, false, null));
    }
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

    WriteIfMatch(outputDir, "FULL.yaffs2", image, files);

    Yaffs2Scanner.ScanResult scan;
    try {
      scan = Yaffs2Scanner.Scan(image);
    } catch {
      WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"), files);
      return;
    }

    WriteIfMatch(outputDir, "metadata.ini", BuildMetadata(scan), files);
    if (scan.Objects.Count > 0)
      WriteIfMatch(outputDir, "directory_tree.txt", BuildTree(scan), files);

    var paths = BuildPaths(scan);
    foreach (var obj in scan.Objects) {
      if (obj.Type != Yaffs2Scanner.YObjectType.File) continue;
      if (!scan.DataChunks.TryGetValue(obj.ObjectId, out var chunks) || chunks.Count == 0) continue;
      var path = paths.TryGetValue(obj.ObjectId, out var p) ? p : obj.Name;
      if (string.IsNullOrEmpty(path)) continue;

      var data = Concat(chunks, obj.Size);
      WriteIfMatch(outputDir, "files/" + path, data, files);
    }
  }

  private static void WriteIfMatch(string outputDir, string name, byte[] data, string[]? filter) {
    if (filter != null && filter.Length > 0 && !MatchesFilter(name, filter)) return;
    WriteFile(outputDir, name, data);
  }

  private static byte[] Concat(List<byte[]> chunks, long declaredSize) {
    var total = chunks.Sum(c => (long)c.Length);
    // Trim to declared size if smaller.
    var targetLen = declaredSize > 0 && declaredSize < total ? (int)declaredSize : (int)total;
    var result = new byte[targetLen];
    var pos = 0;
    foreach (var c in chunks) {
      var take = Math.Min(c.Length, targetLen - pos);
      if (take <= 0) break;
      Buffer.BlockCopy(c, 0, result, pos, take);
      pos += take;
    }
    return result;
  }

  private static Dictionary<int, string> BuildPaths(Yaffs2Scanner.ScanResult scan) {
    // Rebuild each object's path by walking parents.
    var byId = new Dictionary<int, Yaffs2Scanner.ObjectEntry>();
    foreach (var o in scan.Objects) byId[o.ObjectId] = o;
    var paths = new Dictionary<int, string>();
    foreach (var o in scan.Objects) {
      var segments = new List<string>();
      var cur = o;
      var guard = 0;
      while (cur != null && guard++ < 256) {
        if (string.IsNullOrEmpty(cur.Name)) break;
        segments.Add(cur.Name);
        if (cur.ParentId == 1 || cur.ParentId == 0 || cur.ParentId == cur.ObjectId) break;
        if (!byId.TryGetValue(cur.ParentId, out var parent)) break;
        cur = parent;
      }
      segments.Reverse();
      paths[o.ObjectId] = string.Join('/', segments);
    }
    return paths;
  }

  private static byte[] BuildMetadata(Yaffs2Scanner.ScanResult scan) {
    var sb = new StringBuilder();
    sb.Append(CultureInfo.InvariantCulture, $"parse_status={(scan.ParseOk ? "ok" : "partial")}\n");
    sb.Append(CultureInfo.InvariantCulture, $"chunk_size={scan.ChunkSize}\n");
    sb.Append(CultureInfo.InvariantCulture, $"spare_size={scan.SpareSize}\n");
    sb.Append(CultureInfo.InvariantCulture, $"chosen_layout={scan.ChunkSize}+{scan.SpareSize}\n");
    sb.Append(CultureInfo.InvariantCulture, $"object_count={scan.Objects.Count}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static byte[] BuildTree(Yaffs2Scanner.ScanResult scan) {
    var sb = new StringBuilder();
    foreach (var o in scan.Objects)
      sb.Append(CultureInfo.InvariantCulture, $"{o.ObjectId}\t{o.ParentId}\t{o.Type}\t{o.Name}\t{o.Size}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static byte[] ReadAll(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }
}
