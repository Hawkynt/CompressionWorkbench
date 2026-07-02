#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Gfs1;

/// <summary>
/// Sistina/Red Hat GFS (pre-GFS2) format descriptor. WORM writer + reader with
/// real nested subdirectories, defrag/purge/conversion, fileset optimizer,
/// and an options schema (BlockSize / JournalCount / LockProto / LockTable).
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://sourceforge.net/projects/opengfs/</c> — OpenGFS, the open continuation of Sistina GFS whose headers define the GFS1 on-disk structures</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Global_File_System_2</c> — Wikipedia article covering GFS history and its GFS2 successor</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para><b>Reference</b>: Sistina GFS / OpenGFS (the pre-Red Hat patches).
/// Meta-header magic <c>0x01161970</c> appears at every metadata block start.
/// Superblock at byte offset 65536. GFS vs GFS2 disambiguated by
/// <c>sb_multihost_format = 1900</c> (GFS) vs <c>1901</c> (GFS2). We anchor
/// the magic at offset 65536 + 0x40 so detection doesn't collide with
/// <c>FileSystem.Gfs2</c>.</para>
/// <para><b>Hierarchy</b>: real — directories nest via the writer's inode +
/// (4-byte BE inode + 1-byte nlen + name) dirent chain (single-block dirs
/// cap one BB of entries).</para>
/// <para><b>Lock proto / table</b>: GFS1 requires <c>sb_lockproto</c>
/// (<c>"lock_nolock"</c> for standalone, <c>"lock_dlm"</c> for clustered) +
/// <c>sb_locktable</c>. The writer emits these via the options schema; the
/// real distributed-lock protocol negotiation is out of WORM scope.</para>
/// </remarks>
public sealed class Gfs1FormatDescriptor :
    IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable,
    IFilesystemExtentMap, IWipeEmpty, IFormatOptionsSchema, ILayoutOptimizable {

  public string Id => "Gfs1";
  public string DisplayName => "GFS (Sistina/Red Hat, original)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".gfs";
  public IReadOnlyList<string> Extensions => [".gfs", ".gfs1"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x01, 0x16, 0x19, 0x70], Offset: 65536 + 0x40, Confidence: 0.65),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Sistina GFS (pre-GFS2) — WORM writer + nested-directory reader.";

  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new("BlockSize", "Block size", FormatOptionKind.Enum, "4096", AllowedValues: ["4096"],
      Description: "GFS1 block size (always 4096 per Sistina spec)."),
    new("JournalCount", "Journal count", FormatOptionKind.Integer, "1",
      Description: "Number of per-node journals to allocate (1 standalone; >1 for clustered)."),
    new("LockProto", "Lock protocol", FormatOptionKind.Enum, "lock_nolock",
      AllowedValues: ["lock_nolock", "lock_dlm"],
      Description: "Cluster lock protocol. Use lock_nolock for single-node images."),
    new("LockTable", "Lock table", FormatOptionKind.String, "WORM:gfs1",
      Description: "Lock table identifier (format: clustername:fsname)."),
    FilesystemSchemaPresets.VolumeLabel(16),
  ];

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    try {
      var r = new Gfs1Reader(stream);
      return r.Entries.Select((e, i) => new ArchiveEntryInfo(
        i, e.Name, e.Size, e.Size, "stored", e.IsDirectory, false, null)).ToList();
    } catch {
      return [
        new ArchiveEntryInfo(0, "FULL.gfs", stream.Length, stream.Length, "stored", false, false, null),
        new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null),
      ];
    }
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    try {
      var r = new Gfs1Reader(stream);
      foreach (var e in r.Entries) {
        if (e.IsDirectory) continue;
        if (files != null && !MatchesFilter(e.Name, files)) continue;
        WriteFile(outputDir, e.Name, r.Extract(e));
      }
    } catch {
      stream.Position = 0;
      using var ms = new MemoryStream();
      stream.CopyTo(ms);
      var sb = Gfs1Superblock.TryParse(ms.ToArray());
      WriteFile(outputDir, "metadata.ini", BuildMetadata(sb));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new Gfs1Writer();
    w.SetVolumeLabel(options.GetOption("VolumeLabel", "WORM"));
    w.SetJournalCount(options.GetOptionInt("JournalCount", 1));
    w.SetLockProto(options.GetOption("LockProto", "lock_nolock"));
    w.SetLockTable(options.GetOption("LockTable", "WORM:gfs1"));
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      w.AddFile(i.ArchiveName, i.ReadContent());
    }
    w.WriteTo(output);
  }

  // ── IArchiveModifiable (genuine in-place R/W) ───────────────────────────
  //
  // Gfs1InPlaceModifier writes only the changed inode slot(s), the parent dir
  // block, the appended data run, and sb_size — every untouched block stays
  // byte-identical at its original offset. Root files and one level of nested
  // directories are handled in place; deeper trees, a full inode region, or a
  // full directory block fall back to the rebuild delegate.

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs)
    => Gfs1InPlaceModifier.Add(archive, inputs,
        (a, i) => ModifyRebuilder.Add(a, i, ReadEntries, BuildImage));

  public void Remove(Stream archive, string[] entryNames)
    => Gfs1InPlaceModifier.Remove(archive, entryNames,
        (a, n) => ModifyRebuilder.Remove(a, n, ReadEntries, BuildImage));

  private static IEnumerable<(string Name, byte[] Data)> ReadEntries(Stream stream) {
    var r = new Gfs1Reader(stream);
    return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e))).ToList();
  }

  private static byte[] BuildImage(IReadOnlyList<(string Name, byte[] Data)> files) {
    var w = new Gfs1Writer();
    foreach (var (n, d) in files) w.AddFile(n, d);
    return w.Build();
  }

  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) => Gfs1ExtentMap.Enumerate(image);

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  public void Defragment(Stream archive, DefragOptions options)
    => DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new Gfs1Reader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        var w = new Gfs1Writer();
        foreach (var (n, d) in files) w.AddFile(n, d);
        return w.Build();
      });

  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var size = image.Length;
    var extents = Gfs1ExtentMap.Enumerate(image);
    return UnusedSpaceWiper.Wipe(image, extents, size, wipeClusterTips: false, fileSizeLookup: null);
  }

  private static byte[] BuildMetadata(Gfs1Superblock sb) {
    var bldr = new StringBuilder();
    bldr.Append(CultureInfo.InvariantCulture, $"parse_status={(sb.Valid ? "ok" : "partial")}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"mh_magic=0x{sb.MhMagic:X8}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"sb_offset=65536\n");
    bldr.Append(CultureInfo.InvariantCulture, $"sb_multihost_format={sb.MultihostFormat}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"sb_fs_format={sb.FsFormat}\n");
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }
}
