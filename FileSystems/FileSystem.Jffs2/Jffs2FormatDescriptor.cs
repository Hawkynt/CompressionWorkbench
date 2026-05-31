#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Jffs2;

/// <summary>
/// JFFS2 (Journaling Flash File System v2) format descriptor.
/// Supports: list, extract, create (WORM), modify (rebuild-based), defragment, extent map.
/// </summary>
public sealed class Jffs2FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IWipeEmpty {
  public string Id => "Jffs2";
  public string DisplayName => "JFFS2";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
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
  public string Description => "Journaling Flash File System v2 — log-structured flash filesystem.";

  // ── IArchiveFormatOperations (List / Extract) ─────────────────────────

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

    // Also list actual files from the file reader
    try {
      var reader = new Jffs2FileReader(image);
      foreach (var entry in reader.Entries) {
        if (entry.IsDirectory) continue;
        var data = reader.Extract(entry);
        entries.Add(new ArchiveEntryInfo(entries.Count, entry.Name, data.LongLength, data.LongLength, "stored", false, false, null));
      }
    } catch {
      // Fall back to triage-only listing
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

    // Also extract actual files
    try {
      var reader = new Jffs2FileReader(image);
      foreach (var entry in reader.Entries) {
        if (entry.IsDirectory) continue;
        if (files != null && files.Length > 0 && !MatchesFilter(entry.Name, files)) continue;
        var data = reader.Extract(entry);
        WriteFile(outputDir, entry.Name, data);
      }
    } catch {
      // Fall back to triage-only extraction
    }
  }

  // ── IArchiveCreatable ─────────────────────────────────────────────────

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new Jffs2Writer();
    foreach (var (name, data) in FilesOnly(inputs))
      w.AddFile(name, data);
    w.WriteTo(output);
  }

  // ── IArchiveModifiable (rebuild-based) ────────────────────────────────

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs)
    => ModifyRebuilder.Add(archive, inputs,
      readEntries: ReadFileEntries,
      buildImage: BuildImage);

  public void Remove(Stream archive, string[] entryNames)
    => ModifyRebuilder.Remove(archive, entryNames,
      readEntries: ReadFileEntries,
      buildImage: BuildImage);

  // ── IArchiveDefragmentable ────────────────────────────────────────────

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  public void Defragment(Stream archive, DefragOptions options)
    => DefragRebuilder.Rebuild(archive, options,
      readEntries: ReadFileEntries,
      buildImage: BuildImage);

  // ── IFilesystemExtentMap ──────────────────────────────────────────────

  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) {
    byte[] data;
    try {
      image.Position = 0;
      using var ms = new MemoryStream();
      image.CopyTo(ms);
      data = ms.ToArray();
    } catch {
      return [];
    }

    return EnumerateExtentsCore(data);
  }

  private static List<DefragBlockInfo> EnumerateExtentsCore(byte[] data) {
    var result = new List<DefragBlockInfo>();
    var off = 0;
    while (off + 12 <= data.Length) {
      var magic = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(off, 2));
      if (magic != 0x1985) {
        // Check if this is 0xFF-filled free space
        if (off + 4 <= data.Length && data[off] == 0xFF && data[off + 1] == 0xFF && data[off + 2] == 0xFF && data[off + 3] == 0xFF) {
          var freeStart = off;
          while (off < data.Length && data[off] == 0xFF)
            off++;
          off = (off + 3) & ~3;
          if (off > freeStart)
            result.Add(new DefragBlockInfo(freeStart, off - freeStart, DefragBlockKind.Free));
          continue;
        }
        off += 4;
        continue;
      }

      var nodeType = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(off + 2, 2));
      var totLen = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off + 4, 4));

      if (totLen < 12 || totLen > data.Length || off + (int)totLen > data.Length) {
        off += 4;
        continue;
      }

      var aligned = ((int)totLen + 3) & ~3;

      switch (nodeType) {
        case 0x2003: // CLEANMARKER
          result.Add(new DefragBlockInfo(off, aligned, DefragBlockKind.MetadataReserved, "cleanmarker"));
          break;
        case 0xE001: // DIRENT
          var name = TryGetDirentName(data, off);
          result.Add(new DefragBlockInfo(off, aligned, DefragBlockKind.MetadataReserved, name != null ? $"dirent:{name}" : "dirent"));
          break;
        case 0xE002: // INODE
          var ino = off + 16 <= data.Length ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off + 12, 4)) : 0u;
          result.Add(new DefragBlockInfo(off, aligned, DefragBlockKind.Used, $"inode:{ino}"));
          break;
        case 0x2004: // PADDING
          result.Add(new DefragBlockInfo(off, aligned, DefragBlockKind.Free, "padding"));
          break;
        default:
          result.Add(new DefragBlockInfo(off, aligned, DefragBlockKind.MetadataReserved, $"node:0x{nodeType:X4}"));
          break;
      }

      off += aligned;
    }

    // Trailing free space
    if (off < data.Length)
      result.Add(new DefragBlockInfo(off, data.Length - off, DefragBlockKind.Free));

    return result;
  }

  // ── IWipeEmpty ────────────────────────────────────────────────────────

  /// <summary>
  /// Zeros the unused (free) regions of a JFFS2 image. JFFS2 is a
  /// log-structured flash filesystem: file data lives in variably-sized inode
  /// nodes packed back to back, with no fixed cluster/block allocation — there
  /// are no cluster tips to wipe. Free space is the erased-flash tail and any
  /// padding/clean gaps, which the generic wiper zero-fills.
  ///
  /// <para>Cluster-tip wiping is therefore N/A here: no file-size lookup is
  /// supplied and <paramref name="wipeClusterTips"/> is forced off so the
  /// per-node log layout is never mistaken for cluster-aligned runs.</para>
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;
    var extents = this.EnumerateExtents(image);
    // Log-structured: no cluster tips. Zero only free regions.
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips: false, fileSizeLookup: null);
  }

  // ── Shared helpers ────────────────────────────────────────────────────

  private static IEnumerable<(string Name, byte[] Data)> ReadFileEntries(Stream stream) {
    var reader = new Jffs2FileReader(stream);
    return reader.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, reader.Extract(e)));
  }

  private static byte[] BuildImage(IReadOnlyList<(string Name, byte[] Data)> files) {
    var w = new Jffs2Writer();
    foreach (var (n, d) in files) w.AddFile(n, d);
    return w.Build();
  }

  private static string? TryGetDirentName(byte[] data, int off) {
    try {
      if (off + 40 > data.Length) return null;
      var nsize = data[off + 28];
      if (nsize == 0 || nsize > 128 || off + 40 + nsize > data.Length) return null;
      return Encoding.UTF8.GetString(data, off + 40, nsize);
    } catch {
      return null;
    }
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
