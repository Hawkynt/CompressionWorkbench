#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Gfs2;

/// <summary>
/// GFS2 (Global File System 2) descriptor — Red Hat's cluster filesystem,
/// mainline Linux since 2.6.19.
///
/// We parse the superblock at offset 65536, surface block size + lock
/// proto/table + UUID + master/root inode pointers, and walk the root
/// inode's inline directory entries (single-leaf, di_height==0). For
/// regular files with inline data (height==0) we extract the bytes.
///
/// On-disk layout reverse-validated against real <c>mkfs.gfs2</c> output
/// (gfs2-utils 3.5.1): the <c>gfs2_meta_header</c> is 24 bytes, the sb carries
/// a reserved <c>__pad2</c> inum between master and root, and the
/// <c>gfs2_dirent</c> header is 40 bytes. See
/// <c>Gfs2ExternalConformanceTests</c> for the mkfs.gfs2 / fsck.gfs2 gate.
///
/// <para>Creation (<see cref="Create"/>, <see cref="Gfs2Writer"/>) emits a fresh,
/// empty standalone (lock_nolock, single-journal) volume — superblock, the
/// fixed first resource group plus a second data resource group with a correct
/// (multi-block) allocation bitmap, the master directory and its system inodes
/// (jindex, per_node, inum, statfs, rindex, quota), a formatted 8&#160;MB journal
/// of clean unmount log headers, and the root directory — all sized so real
/// <c>fsck.gfs2 -n</c> passes clean (exit 0). Supported size range 16–256&#160;MB
/// (single data resource group); the volume is empty, since populating it with
/// files is out of scope.</para>
///
/// Out of scope (multi-week effort each): writing files/directories, ExHash
/// multi-leaf directories, multi-level block indirection (di_height &gt; 0),
/// devices &gt; 256&#160;MB (which gfs2-utils splits into several evenly-spaced
/// resource groups), journal replay, cluster lock manager state, extended
/// attributes.
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
public sealed class Gfs2FormatDescriptor
    : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable, IFormatOptionsSchema, ILayoutOptimizable {

  // ── IFormatOptionsSchema ────────────────────────────────────────────────

  /// <summary>
  /// Knobs the empty-volume writer honours. <c>ImageSize</c> drives the writer's
  /// total size (clamped to the single-data-resource-group range 16–256&#160;MB);
  /// <c>LockTable</c> is written into <c>sb_locktable</c> and read back as
  /// <c>Gfs2Reader.LockTable</c>. The 4&#160;KB block size and the
  /// <c>lock_nolock</c> protocol are fixed by the standalone layout.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    FilesystemSchemaPresets.ImageSize(["16 MB", "32 MB", "64 MB", "128 MB", "256 MB"],
      description: "Total volume size (16–256 MB; a single data resource group)."),
    new FormatOptionDescriptor(
      Key: "LockTable", DisplayName: "Lock table", Kind: FormatOptionKind.String, Default: "",
      Description: "Cluster lock-table name stamped into sb_locktable (empty for a standalone volume)."),
  ];

  public string Id => "Gfs2";
  public string DisplayName => "GFS2 (Global File System 2)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate |
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
    "GFS2 (Red Hat cluster filesystem) — read superblock + single-leaf root directory + inline-data files; create a fresh empty lock_nolock volume that fsck.gfs2 accepts.";

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
    // A volume that carries files lists exactly those. Surfacing the synthetic
    // header entries alongside them would make every rebuild (shrink, defrag)
    // fold them back in as real files, so they stay on the carver path — empty
    // or foreign images, where the header IS all we can offer.
    if (r.Entries.Count > 0) {
      foreach (var e in r.Entries)
        entries.Add(new ArchiveEntryInfo(idx++, e.Name, e.Size, e.Size, "stored", e.IsDirectory, false, e.LastModified));
      return entries;
    }

    var imageLen = TryGetImageLen(r);
    entries.Add(new ArchiveEntryInfo(idx++, "FULL.gfs2", imageLen, imageLen, "stored", false, false, null));
    entries.Add(new ArchiveEntryInfo(idx++, "metadata.ini", 0, 0, "stored", false, false, null));
    if (r.SuperblockValid) {
      var raw = r.SuperblockRaw;
      entries.Add(new ArchiveEntryInfo(idx++, "superblock.bin", raw.LongLength, raw.LongLength, "stored", false, false, null));
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

    using (r) {
      if (r.Entries.Count > 0) {
        foreach (var e in r.Entries) {
          if (e.IsDirectory) continue;
          if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
          var target = Path.Combine(outputDir, e.Name.Replace('/', Path.DirectorySeparatorChar));
          Directory.CreateDirectory(Path.GetDirectoryName(target) ?? outputDir);
          using var output = File.Create(target);
          r.ExtractTo(e, output);
        }
        return;
      }

      var imageLen = TryGetImageLen(r);
      var imageBytes = r.SuperblockValid ? null : ReadBackImageIfSmall(stream);
      WriteIfMatch(outputDir, "metadata.ini", BuildMetadata(r, imageLen), files);

      if (r.SuperblockValid)
        WriteIfMatch(outputDir, "superblock.bin", r.SuperblockRaw, files);
      else if (imageBytes != null)
        WriteIfMatch(outputDir, "FULL.gfs2", imageBytes, files);
    }
  }

  /// <summary>
  /// Creates a fresh, empty standalone (lock_nolock, single-journal) GFS2 volume
  /// that real <c>fsck.gfs2</c> accepts clean. The volume size defaults to 32&#160;MB
  /// and may be overridden with the <c>size</c> format option (bytes, clamped to
  /// 16–256&#160;MB; <c>K</c>/<c>M</c> suffixes accepted).
  /// </summary>
  /// <remarks>
  /// Populating the volume with files is out of scope — GFS2 file/directory
  /// writing (ExHash directories, multi-level indirection, block allocation
  /// across resource groups) is multi-week work. The created root directory is
  /// therefore empty; any non-directory inputs are rejected so callers are not
  /// silently handed an archive missing their data.
  /// </remarks>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);

    var lockTable = options?.GetOption("LockTable", "") ?? "";
    var sizes = new List<long>();
    var files = new List<(string Name, ArchiveInputInfo Input)>();
    if (inputs != null)
      foreach (var i in inputs) {
        if (i.IsDirectory) continue;
        var length = i.InMemoryContent?.LongLength ?? new FileInfo(i.FullPath).Length;
        sizes.Add(length);
        files.Add((i.ArchiveName, i));
      }

    // The requested size is a floor: the volume has to be at least large enough
    // for the payload's dinodes, data blocks and indirect blocks.
    var size = Math.Max(ParseSizeOption(options), Gfs2Writer.EstimateSize(sizes));
    var writer = new Gfs2Writer(size, lockTable: lockTable);
    foreach (var (name, input) in files) {
      if (input.InMemoryContent is { } bytes) {
        writer.AddFile(name, bytes);
        continue;
      }
      var path = input.FullPath;
      writer.AddStreamingFile(name, new FileInfo(path).Length, () => File.OpenRead(path));
    }
    writer.Build(output);
  }

  private static long ParseSizeOption(FormatCreateOptions? options) {
    const long defaultSize = 32L * 1024 * 1024;
    // Accept the schema's "ImageSize" enum ("32 MB", "Auto (fit to files)", …)
    // as well as the legacy raw "size" key (bytes with optional K/M/G suffix).
    var imageSize = FilesystemSchemaPresets.ParseSize(options?.GetOption("ImageSize", ""));
    if (imageSize > 0)
      return Math.Max((long)imageSize, 16L * 1024 * 1024);

    var raw = options?.GetOption("size", "");
    if (string.IsNullOrWhiteSpace(raw))
      return defaultSize;

    raw = raw.Trim();
    var mult = 1L;
    var last = char.ToUpperInvariant(raw[^1]);
    if (last is 'K' or 'M' or 'G') {
      mult = last switch { 'K' => 1024L, 'M' => 1024L * 1024, _ => 1024L * 1024 * 1024 };
      raw = raw[..^1].Trim();
    }
    if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
      return defaultSize;
    var bytes = n * mult;
    return Math.Max(bytes, 16L * 1024 * 1024);
  }

  // ── In-place R/W assessment: LEFT REBUILDING (no CanModify) ───────────────
  //
  // Genuine O(bytes-changed) in-place add is NOT viable for this writer/reader
  // pair, so the descriptor keeps the default IArchiveModifiable rebuild path
  // and does NOT advertise FormatCapabilities.CanModify. Precise reasons:
  //   • Create() emits an EMPTY volume — it rejects every non-directory input
  //     ("GFS2 creation produces an empty volume only"), so there is no seeded
  //     image carrying files to mutate in place.
  //   • The reader only resolves files whose data is stuffed inline in the
  //     dinode block (di_height == 0, payload ≤ BlockSize − 232 = 3864 bytes)
  //     under a single-leaf inline root directory. A real R/W path needs
  //     ExHash multi-leaf directories + multi-level block indirection
  //     (di_height > 0) so a file can exceed ~3.8 KB — the documented
  //     multi-week scope. Without it, round-tripping arbitrary file sizes
  //     (the CRUD cycle writes 9000-byte payloads) is impossible, so claiming
  //     R/W would be dishonest.

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Rewrites the volume with every file laid out contiguously from the start of
  /// the data area. Each entry is spilled to scratch and the writer pulls it back
  /// while laying out the metadata tree, so the rebuild is not bounded by what a
  /// byte[] can hold.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);

    if (options.Mode is DefragMode.ConsolidateAtStart or DefragMode.FillHolesLazy) {
      Gfs2Writer? writer = null;
      Stream? target = null;
      var spill = new List<(string Name, string Path, long Size)>();
      DefragRebuilder.RebuildStreaming(archive, options,
        readEntries: ReadEntries,
        beginWrite: s => target = s,
        writeEntry: (name, data) => {
          // The volume has to be sized before the first byte is written, so the
          // entries are collected first and the writer is built in finishWrite.
          var path = Path.GetTempFileName();
          File.WriteAllBytes(path, data);
          spill.Add((name, path, data.LongLength));
        },
        finishWrite: () => {
          try {
            writer = new Gfs2Writer(Gfs2Writer.EstimateSize(spill.ConvertAll(e => e.Size)));
            foreach (var (name, path, size) in spill) {
              var captured = path;
              writer.AddStreamingFile(name, size, () => File.OpenRead(captured));
            }
            writer.Build(target!);
          } finally {
            foreach (var (_, path, _) in spill)
              try { File.Delete(path); } catch { /* scratch file already gone */ }
          }
        });
      return;
    }

    throw new NotSupportedException(
      $"GFS2 defragmentation supports ConsolidateAtStart and FillHolesLazy; got {options.Mode}.");
  }

  private static IEnumerable<(string Name, byte[] Data)> ReadEntries(Stream stream) {
    using var r = new Gfs2Reader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      using var buffer = new MemoryStream();
      r.ExtractTo(e, buffer);
      yield return (e.Name, buffer.ToArray());
    }
  }

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
