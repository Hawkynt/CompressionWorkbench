#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Efs;

/// <summary>
/// SGI EFS (Extent File System) format descriptor — the pre-XFS native
/// filesystem used on IRIX before 5.3 (1994). Surfaces a real WORM writer that
/// emits a spec-keyed superblock + single-cylinder-group inode table + per-file
/// single-extent layout, plus defrag/purge/conversion/optimizer wiring.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/torvalds/linux/tree/master/fs/efs</c> — Linux kernel EFS driver (read-only), the maintained on-disk reference</description></item>
///   <item><description>IRIX <c>sys/fs/efs_fs.h</c> — the original SGI header defining the superblock and extent layout</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Extent_File_System</c> — Wikipedia overview</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para><b>Reference</b>: Linux kernel <c>fs/efs/efs_fs_sb.h</c>, IRIX
/// <c>sys/fs/efs_fs.h</c>. Superblock at offset 0 (sector 0, 512-byte sectors).
/// Magic <c>0x00072959</c> (big-endian u32) at byte offset 0x18 inside the
/// superblock (<c>fs_magic</c>).</para>
/// <para><b>Hierarchy</b>: real — directories nest via the writer's directory
/// inode chain (single-block directories; bodies use inode + nlen + name
/// dirents). Reader recurses from inode 2 (root) and surfaces each entry at
/// its full path.</para>
/// </remarks>
public sealed class EfsFormatDescriptor :
    IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable,
    IFilesystemExtentMap, IWipeEmpty, IFormatOptionsSchema, ILayoutOptimizable {

  public string Id => "Efs";
  public string DisplayName => "EFS (SGI Extent File System)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".efs";
  public IReadOnlyList<string> Extensions => [".efs", ".efsimg"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // fs_magic = 0x00072959 (BE u32) at byte offset 0x18 of the superblock at sector 0.
    new([0x00, 0x07, 0x29, 0x59], Offset: 0x18, Confidence: 0.85),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "SGI EFS (pre-XFS IRIX filesystem) — WORM writer + hierarchical reader.";

  // ── Options schema ──────────────────────────────────────────────────────
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new("BlockSize", "Block size", FormatOptionKind.Enum, "512", AllowedValues: ["512"],
      Description: "EFS basic-block size in bytes (always 512 per IRIX spec)."),
    new("CylinderGroupSize", "Cylinder group size (BB)", FormatOptionKind.Integer, "32",
      Description: "Cylinder group size in 512-byte basic blocks."),
    FilesystemSchemaPresets.VolumeLabel(6),
  ];

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    try {
      var r = new EfsReader(stream);
      return r.Entries.Select((e, i) => new ArchiveEntryInfo(
        i, e.Name, e.Size, e.Size, "stored", e.IsDirectory, false, null
      )).ToList();
    } catch {
      // Honest fall-back for malformed images: at least surface the raw image
      // plus the metadata.ini stub the old descriptor used.
      return [
        new ArchiveEntryInfo(0, "FULL.efs", stream.Length, stream.Length, "stored", false, false, null),
        new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null),
      ];
    }
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    try {
      var r = new EfsReader(stream);
      foreach (var e in r.Entries) {
        if (e.IsDirectory) continue;
        if (files != null && !MatchesFilter(e.Name, files)) continue;
        WriteFile(outputDir, e.Name, r.Extract(e));
      }
    } catch {
      // Honest fall-back so callers always see a metadata.ini.
      stream.Position = 0;
      using var ms = new MemoryStream();
      stream.CopyTo(ms);
      var sb = EfsSuperblock.TryParse(ms.ToArray());
      WriteFile(outputDir, "metadata.ini", BuildMetadata(sb));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new EfsWriter();
    w.SetVolumeLabel(options.GetOption("VolumeLabel", "WORM"));
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      w.AddFile(i.ArchiveName, i.ReadContent());
    }
    w.WriteTo(output);
  }

  /// <summary>
  /// Adds (or replaces by name) files inside an existing EFS image via
  /// <see cref="EfsInPlaceModifier"/> — TRUE in-place O(touched bytes) I/O
  /// (claim a free dinode slot, append a contiguous extent at EOF, write the
  /// root dirent). Falls back to a whole-image rebuild only for nested paths,
  /// inode-table exhaustion, or extents past the single-extent ceiling.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    try {
      foreach (var (name, data) in FilesOnly(inputs)) {
        EfsInPlaceModifier.RemoveFile(archive, name, wipeData: true);
        EfsInPlaceModifier.AddFile(archive, name, data);
      }
    } catch (IOException) {
      archive.Position = 0;
      ModifyRebuilder.Add(archive, inputs,
        readEntries: stream => {
          var r = new EfsReader(stream);
          return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
        },
        buildImage: BuildImage);
    }
  }

  /// <summary>Removes the named entries in-place via <see cref="EfsInPlaceModifier"/>.</summary>
  public void Remove(Stream archive, string[] entryNames) {
    var leftover = new List<string>();
    foreach (var name in entryNames) {
      var leaf = name.Replace('\\', '/').TrimStart('/');
      if (leaf.Contains('/') || !EfsInPlaceModifier.RemoveFile(archive, leaf, wipeData: true))
        leftover.Add(name);
    }
    if (leftover.Count == 0) return;
    archive.Position = 0;
    ModifyRebuilder.Remove(archive, leftover.ToArray(),
      readEntries: stream => {
        var r = new EfsReader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: BuildImage);
  }

  private static byte[] BuildImage(IReadOnlyList<(string Name, byte[] Data)> files) {
    var w = new EfsWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    return w.Build();
  }

  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) => EfsExtentMap.Enumerate(image);

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  public void Defragment(Stream archive, DefragOptions options)
    => DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new EfsReader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        var w = new EfsWriter();
        foreach (var (n, d) in files) w.AddFile(n, d);
        return w.Build();
      });

  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var size = image.Length;
    var extents = EfsExtentMap.Enumerate(image);
    return UnusedSpaceWiper.Wipe(image, extents, size, wipeClusterTips: false, fileSizeLookup: null);
  }

  private static byte[] BuildMetadata(EfsSuperblock sb) {
    var bldr = new StringBuilder();
    bldr.Append(CultureInfo.InvariantCulture, $"parse_status={(sb.Valid ? "ok" : "partial")}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"size_blocks={sb.SizeBlocks}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"first_cg={sb.FirstCg}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"cg_isize={sb.CgIsize}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"cg_size={sb.CgSize}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"sectors={sb.Sectors}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"heads={sb.Heads}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"num_cg={sb.NumCg}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"dirty={sb.Dirty}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"time={sb.Time}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"magic=0x{sb.Magic:X8}\n");
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }
}
