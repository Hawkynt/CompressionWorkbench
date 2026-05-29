#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Gfs2;

/// <summary>
/// Read-only descriptor for GFS2 (Global File System 2) — Red Hat's
/// cluster filesystem, mainline Linux since 2.6.19.
///
/// We parse the superblock at offset 65536, surface block size + lock
/// proto/table + UUID + master/root inode pointers, and walk the root
/// inode's inline directory entries (single-leaf, di_height==0). For
/// regular files with inline data (height==0) we extract the bytes.
///
/// Out of scope (multi-week effort each): ExHash multi-leaf directories,
/// multi-level block indirection (di_height &gt; 0), journal replay, cluster
/// lock manager state, extended attributes.
///
/// Magic: <c>mh_magic = 0x01161970</c> (BE u32) at the start of the
/// superblock meta header. On disk at byte offset 65536 this serialises as
/// <c>01 16 19 70</c>. Confidence 0.85 — well-known constant at a fixed
/// offset, but GFS2 shares this magic with GFS1 at slightly different
/// layouts, so we keep a small margin below the 0.9-0.95 reserved for
/// formats with a structurally unique header.
///
/// References:
/// <list type="bullet">
///   <item><description>Linux kernel <c>fs/gfs2/</c> — <c>include/uapi/linux/gfs2_ondisk.h</c></description></item>
///   <item><description>Red Hat Cluster Suite / Resilient Storage Add-On documentation</description></item>
/// </list>
/// </summary>
public sealed class Gfs2FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveDefragmentable {
  public string Id => "Gfs2";
  public string DisplayName => "GFS2 (Global File System 2)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".gfs2";
  public IReadOnlyList<string> Extensions => [".gfs2"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // 0x01161970 BE at offset 65536 — start of gfs2_meta_header.mh_magic for the SB.
    new([0x01, 0x16, 0x19, 0x70], Offset: (int)Gfs2Reader.SbByteOffset, Confidence: 0.85),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "GFS2 (Red Hat cluster filesystem) — read-only superblock parse + single-leaf root directory walk + inline-data file extract.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = new List<ArchiveEntryInfo>();
    Gfs2Reader? r;
    try {
      r = new Gfs2Reader(stream);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.gfs2", 0, 0, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    var idx = 0;
    var imageLen = TryGetImageLen(r);
    entries.Add(new ArchiveEntryInfo(idx++, "FULL.gfs2", imageLen, imageLen, "stored", false, false, null));
    entries.Add(new ArchiveEntryInfo(idx++, "metadata.ini", 0, 0, "stored", false, false, null));
    if (r.SuperblockValid) {
      var raw = r.SuperblockRaw;
      entries.Add(new ArchiveEntryInfo(idx++, "superblock.bin", raw.LongLength, raw.LongLength, "stored", false, false, null));
    }
    foreach (var e in r.Entries) {
      entries.Add(new ArchiveEntryInfo(idx++, e.Name, e.Size, e.Size, "stored", e.IsDirectory, false, e.LastModified));
    }
    return entries;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    Gfs2Reader r;
    try {
      r = new Gfs2Reader(stream);
    } catch {
      WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"), files);
      return;
    }

    var imageLen = TryGetImageLen(r);
    var imageBytes = r.SuperblockValid ? null : ReadBackImageIfSmall(stream);
    WriteIfMatch(outputDir, "metadata.ini", BuildMetadata(r, imageLen), files);

    if (r.SuperblockValid) {
      WriteIfMatch(outputDir, "superblock.bin", r.SuperblockRaw, files);
      foreach (var e in r.Entries) {
        if (e.IsDirectory) continue;
        if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
        var data = r.Extract(e);
        WriteFile(outputDir, e.Name, data);
      }
    } else if (imageBytes != null) {
      WriteIfMatch(outputDir, "FULL.gfs2", imageBytes, files);
    }
  }

  // Throws NotSupported per project policy — Create requires a real writer.
  public void Defragment(Stream archive)
    => throw new NotSupportedException("Gfs2 read-only — defragmentation requires a writer.");

  public void Defragment(Stream archive, DefragOptions options)
    => throw new NotSupportedException("Gfs2 read-only — defragmentation requires a writer.");

  private static long TryGetImageLen(Gfs2Reader r)
    => r.SuperblockRaw.LongLength > 0 ? Gfs2Reader.SbByteOffset + r.SuperblockRaw.LongLength : 0;

  private static byte[]? ReadBackImageIfSmall(Stream s) {
    try {
      if (!s.CanSeek) return null;
      s.Position = 0;
      if (s.Length > 64 * 1024) return null;
      var buf = new byte[s.Length];
      var read = 0;
      while (read < buf.Length) {
        var n = s.Read(buf, read, buf.Length - read);
        if (n == 0) break;
        read += n;
      }
      return buf;
    } catch {
      return null;
    }
  }

  private static void WriteIfMatch(string outputDir, string name, byte[] data, string[]? filter) {
    if (filter != null && filter.Length > 0 && !MatchesFilter(name, filter)) return;
    WriteFile(outputDir, name, data);
  }

  private static byte[] BuildMetadata(Gfs2Reader r, long imageSize) {
    var b = new StringBuilder();
    var ic = CultureInfo.InvariantCulture;
    b.Append(ic, $"parse_status={(r.SuperblockValid ? "ok" : "partial")}\n");
    b.Append(ic, $"superblock_valid={r.SuperblockValid}\n");
    if (r.SuperblockValid) {
      b.Append(ic, $"block_size={r.BlockSize}\n");
      b.Append(ic, $"block_size_shift={r.BlockSizeShift}\n");
      b.Append(ic, $"root_inode_block={r.RootInodeBlock}\n");
      b.Append(ic, $"root_formal_ino={r.RootFormalIno}\n");
      b.Append(ic, $"master_inode_block={r.MasterInodeBlock}\n");
      b.Append(ic, $"master_formal_ino={r.MasterFormalIno}\n");
      b.Append(ic, $"lock_proto={r.LockProto}\n");
      b.Append(ic, $"lock_table={r.LockTable}\n");
      b.Append(ic, $"uuid_hex={r.UuidHex}\n");
      b.Append(ic, $"root_entry_count={r.Entries.Count}\n");
    }
    if (imageSize > 0)
      b.Append(ic, $"approx_image_size={imageSize}\n");
    return Encoding.UTF8.GetBytes(b.ToString());
  }
}
